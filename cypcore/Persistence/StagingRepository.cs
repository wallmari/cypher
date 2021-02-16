﻿// CYPCore by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using Microsoft.Extensions.Logging;

using CYPCore.Models;

namespace CYPCore.Persistence
{
    public class StagingRepository : Repository<StagingProto>, IStagingRepository
    {
        private readonly IStoredb _storedb;
        private readonly ILogger _logger;

        public StagingRepository(IStoredb storedb, ILogger logger)
            : base(storedb, logger)
        {
            _storedb = storedb;
            _logger = logger;
        }
    }
}
