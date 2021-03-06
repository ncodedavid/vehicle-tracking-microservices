﻿using System;
using System.Collections.Generic;
using System.Text;

namespace DomainModels.System
{
    public sealed class Identifiers
    {
        #region infrastructure

        public const string CacheServer = "distributed_cache";
        public const string CacheDBVehicles = "cache_db_vehicles";
        public const string MessagesMiddleware = "messages_middleware";
        public const string MiddlewareExchange = "middleware_exchange";
        public const string MessageSubscriberRoutes = "middleware_routes_subscriber";
        public const string MessagePublisherRoute = "middleware_ping_publisher";
        public const string MessagesMiddlewareUsername = "middleware_username";
        public const string MessagesMiddlewarePassword = "middleware_password";
        public const string EventDbConnection = "event_db_connection";
        public const string DefaultJsonObject = "{}";
        #endregion

        #region settings

        public const int RetryCount = 5;
        public const int DataPageSize = 10;
        public const int TimeoutInSec = 60;
        public const int BreakTimeoutInSec = 5;
        public const int CircutBreakerExceptionsCount = 5;
        public const int MaxRowsCount = 1000;
        public const int cache_db_idx0 = 0;
        public const int cache_db_idx1 = 1;
        public const int cache_db_idx2 = 2;
        public const string PingServiceName = "ping";
        public const string TrackingServiceName = "tracking";
        public const string VeihcleServiceName = "vehicle";
        public const string CustomerServiceName = "customer";
        public const string onPing="on_ping";
        #endregion
    }
}
