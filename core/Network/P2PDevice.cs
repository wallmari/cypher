// CypherNetwork by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CypherNetwork.Extensions;
using CypherNetwork.Helper;
using CypherNetwork.Models;
using CypherNetwork.Models.Messages;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IO;
using nng;
using nng.Native;
using Retlang.Net.Channels;
using Serilog;

namespace CypherNetwork.Network;

public struct Message
{
    public Memory<byte> Memory { get; }
    public byte[] PublicKey { get; }

    public Message(Memory<byte> memory, byte[] publicKey)
    {
        Memory = memory;
        PublicKey = publicKey;
    }
}

/// <summary>
/// </summary>
public interface IP2PDevice
{
    Task<Message> DecryptAsync(INngMsg nngMsg);
}

/// <summary>
/// </summary>
public sealed class P2PDevice : IDisposable, IP2PDevice
{
    private static readonly RecyclableMemoryStreamManager Manager = new();

    private readonly ICypherNetworkCore _cypherNetworkCore;
    private readonly ILogger _logger;
    private readonly IList<Worker> _disposableWorkers = new List<Worker>();

    private IRepSocket _repSocket;
    private bool _disposed;

    /// <summary>
    /// </summary>
    /// <param name="cypherNetworkCore"></param>
    public P2PDevice(ICypherNetworkCore cypherNetworkCore)
    {
        _cypherNetworkCore = cypherNetworkCore;
        using var serviceScope = _cypherNetworkCore.ServiceScopeFactory.CreateScope();
        _logger = serviceScope.ServiceProvider.GetService<ILogger>()?.ForContext("SourceContext", nameof(P2PDevice));
        Init();
    }

    /// <summary>
    /// </summary>
    /// <param name="nngMsg"></param>
    /// <returns></returns>
    public unsafe Task<Message> DecryptAsync(INngMsg nngMsg)
    {
        try
        {
            var msg = nngMsg.AsSpan();
            var length = BitConverter.ToInt32(msg);
            if (length != 32) return Task.FromResult(new Message(new Memory<byte>(), Array.Empty<byte>()));
            const int prefixByteLength = 4;
            var pk = stackalloc byte[length];
            var publicKey = new Span<byte>(pk, length);
            msg.Slice(prefixByteLength, length).CopyTo(publicKey);
            length = BitConverter.ToInt32(msg[(prefixByteLength + publicKey.Length)..]);
            ReadOnlySpan<byte> cipher = msg[(prefixByteLength + publicKey.Length + prefixByteLength)..];
            if (cipher.Length != length) return Task.FromResult(new Message(new Memory<byte>(), Array.Empty<byte>()));
            var message = new Message(_cypherNetworkCore.Crypto().BoxSealOpen(cipher,
                _cypherNetworkCore.KeyPair.PrivateKey.FromSecureString().HexToByte(),
                _cypherNetworkCore.KeyPair.PublicKey.AsSpan()[1..33]), publicKey.ToArray());
            return Task.FromResult(message);
        }
        catch (AccessViolationException ex)
        {
            _logger.Fatal("{@Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Here().Error("{@Message}", ex.Message);
        }

        return Task.FromResult(new Message(new Memory<byte>(), Array.Empty<byte>()));
    }

    /// <summary>
    /// </summary>
    private void Init()
    {
        ListeningAsync().SafeFireAndForget(exception => { _logger.Here().Error("{@Message}", exception.Message); });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    private Task ListeningAsync()
    {
        try
        {
            var ipEndPoint = Util.TryParseAddress(_cypherNetworkCore.AppOptions.Gossip.Listening[6..].FromBytes());
            Util.ThrowPortNotFree(ipEndPoint.Port);
            _repSocket = NngFactorySingleton.Instance.Factory.ReplierOpen()
                .ThenListen($"tcp://{ipEndPoint.Address}:{ipEndPoint.Port}", Defines.NngFlag.NNG_FLAG_NONBLOCK).Unwrap();
            for (var i = 0; i < 5; i++) _disposableWorkers.Add(new Worker(_cypherNetworkCore, _repSocket));
        }
        catch (Exception ex)
        {
            _logger.Here().Error("{@Message}", ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// </summary>
    /// <param name="msg"></param>
    /// <param name="command"></param>
    /// <param name="parameters"></param>
    /// <returns></returns>
    private static bool UnWrap(ReadOnlySpan<byte> msg, out ProtocolCommand command, out Parameter[] parameters)
    {
        command = ProtocolCommand.NotFound;
        parameters = default;
        try
        {
            using var stream = Manager.GetStream(msg);
            parameters = MessagePackSerializer.Deserialize<Parameter[]>(stream);
            return Enum.TryParse(Enum.GetName(parameters[0].ProtocolCommand), out command);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Ignore
        }
        catch (Exception)
        {
            // Ignore
        }

        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    private class Worker : IDisposable
    {
        private readonly ICypherNetworkCore _cypherNetworkCore;
        private readonly ILogger _logger;
        private readonly RequestReplyChannel<Tuple<ProtocolCommand, Parameter[]>, ReadOnlySequence<byte>> _cmd = new();
        private readonly IDisposable _disposableSubscribe;
        private readonly IDisposable _disposableInterval;
        private bool _disposed;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cypherNetworkCore"></param>
        /// <param name="repSocket"></param>
        public Worker(ICypherNetworkCore cypherNetworkCore, IRepSocket repSocket)
        {
            _cypherNetworkCore = cypherNetworkCore;
            using var serviceScope = _cypherNetworkCore.ServiceScopeFactory.CreateScope();
            _logger = serviceScope.ServiceProvider.GetService<ILogger>()?.ForContext("SourceContext", nameof(Worker));
            var ctx = repSocket.CreateAsyncContext(NngFactorySingleton.Instance.Factory).Unwrap();
            _disposableSubscribe = _cmd.Subscribe(_cypherNetworkCore.PoolFiber().Value.Result, OnRequest);
            _disposableInterval = Observable.Interval(TimeSpan.Zero, NewThreadScheduler.Default).Subscribe(_ =>
            {
                if (_cypherNetworkCore.ApplicationLifetime.ApplicationStopping.IsCancellationRequested) return;
                try
                {
                    WorkerAsync(ctx).Wait();
                }
                catch (AggregateException)
                {
                    // Ignore
                }
            });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="ctx"></param>
        private async Task WorkerAsync(IRepReqAsyncContext<INngMsg> ctx)
        {
            var nngResult = await ctx.Receive();
            var message = await _cypherNetworkCore.P2PDevice().DecryptAsync(nngResult.Unwrap());
            if (message.Memory.Length == 0)
            {
                (await ctx.Reply(NngFactorySingleton.Instance.Factory.CreateMessage())).Unwrap();
                return;
            }

            if (UnWrap(message.Memory.Span, out var protocolCommand, out var parameters))
            {
                var nngReplyMsg = NngFactorySingleton.Instance.Factory.CreateMessage();
                try
                {
                    var cipher = SendRequest(new Tuple<ProtocolCommand, Parameter[]>(protocolCommand, parameters),
                        message.PublicKey);
                    if (cipher.Length != 0)
                    {
                        IReadOnlyList<byte[]> bytesList = new List<byte[]>(2)
                        {
                            _cypherNetworkCore.KeyPair.PublicKey[1..33].WrapLengthPrefix(), cipher.WrapLengthPrefix()
                        };

                        await using var stream = Util.Manager.GetStream(Util.Combine(bytesList)
                            .AsSpan()) as RecyclableMemoryStream;
                        foreach (var memory in stream.GetReadOnlySequence()) nngReplyMsg.Append(memory.Span);
                        (await ctx.Reply(nngReplyMsg)).Unwrap();
                        return;
                    }
                }
                catch (AggregateException)
                {
                    // Ignore
                }
                catch (Exception ex)
                {
                    _logger.Fatal("{@Message}", ex.Message);
                }
                finally
                {
                    nngReplyMsg.Dispose();
                }
            }

            (await ctx.Reply(NngFactorySingleton.Instance.Factory.CreateMessage())).Unwrap();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="req"></param>
        private async void OnRequest(IRequest<Tuple<ProtocolCommand, Parameter[]>, ReadOnlySequence<byte>> req)
        {
            try
            {
                if (_disposed) return;
                var result =
                    await _cypherNetworkCore.P2PDeviceApi().Commands[(int)req.Request.Item1](req.Request.Item2);
                req.SendReply(result);
            }
            catch (Exception ex)
            {
                _logger.Here().Error("{@Message}", ex.Message);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="command"></param>
        /// <param name="publicKey"></param>
        /// <returns></returns>
        private byte[] SendRequest(Tuple<ProtocolCommand, Parameter[]> command, byte[] publicKey)
        {
            var reply = _cmd.SendRequest(command);
            reply.Receive(10000, out var response);
            var cipher = _cypherNetworkCore.Crypto()
                .BoxSeal(response.IsSingleSegment ? response.First.Span : response.ToArray(), publicKey);
            return cipher;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="disposing"></param>
        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _disposableInterval?.Dispose();
                _disposableSubscribe?.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="disposing"></param>
    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _repSocket?.Dispose();
            foreach (var disposable in _disposableWorkers)
            {
                disposable.Dispose();
            }
        }

        _disposed = true;
    }

    /// <summary>
    /// 
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}