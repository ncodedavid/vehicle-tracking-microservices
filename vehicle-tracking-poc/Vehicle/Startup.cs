﻿using BackgroundMiddleware;
using BuildingAspects.Behaviors;
using BuildingAspects.Services;
using DomainModels.Business;
using DomainModels.Business.CustomerDomain;
using DomainModels.Business.VehicleDomain;
using DomainModels.DataStructure;
using DomainModels.System;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedisCacheAdapter;
using Swashbuckle.AspNetCore.Swagger;
using System;
using System.Collections.Generic;
using System.Linq;
using VehicleSQLDB;
using VehicleSQLDB.DbModels;
using WebComponents.Interceptors;


namespace Vehicle
{
    public class Startup
    {
        private readonly MiddlewareConfiguration _systemLocalConfiguration;
        private readonly IHostingEnvironment _environemnt;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        public IHostingEnvironment Environemnt => _environemnt;
        public IConfiguration Configuration => _configuration;
        public ILogger Logger => _logger;

        private string AssemblyName => $"{Environemnt.ApplicationName} V{this.GetType().Assembly.GetName().Version}";

        public Startup(ILoggerFactory logger, IHostingEnvironment environemnt, IConfiguration configuration)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(environemnt.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            _configuration = builder.Build();

            _environemnt = environemnt;

            _logger = logger
                  .AddConsole()
                  .AddDebug()
                  .AddFile("Logs/Startup-{Date}.txt", isJson: true)
                  .CreateLogger<Startup>();
            //local system configuration
            _systemLocalConfiguration = new ServiceConfiguration().Create(new Dictionary<string, string>() {
                {nameof(_systemLocalConfiguration.CacheServer), Configuration.GetValue<string>(Identifiers.CacheServer)},
                {nameof(_systemLocalConfiguration.VehiclesCacheDB),  Configuration.GetValue<string>(Identifiers.CacheDBVehicles)},
                {nameof(_systemLocalConfiguration.EventDbConnection),  Configuration.GetValue<string>(Identifiers.EventDbConnection)},
                {nameof(_systemLocalConfiguration.MessagesMiddleware),  Configuration.GetValue<string>(Identifiers.MessagesMiddleware)},
                {nameof(_systemLocalConfiguration.MiddlewareExchange),  Configuration.GetValue<string>(Identifiers.MiddlewareExchange)},
                {nameof(_systemLocalConfiguration.MessagePublisherRoute),  Configuration.GetValue<string>(Identifiers.MessagePublisherRoute)},
                {nameof(_systemLocalConfiguration.MessageSubscriberRoute),  Configuration.GetValue<string>(Identifiers.MessageSubscriberRoutes)},
                {nameof(_systemLocalConfiguration.MessagesMiddlewareUsername),  Configuration.GetValue<string>(Identifiers.MessagesMiddlewareUsername)},
                {nameof(_systemLocalConfiguration.MessagesMiddlewarePassword),  Configuration.GetValue<string>(Identifiers.MessagesMiddlewarePassword)},
            });
        }

        // Inject background service, for receiving message
        public void ConfigureServices(IServiceCollection services)
        {
            var serviceProvider = services.BuildServiceProvider();
            var loggerFactorySrv = serviceProvider.GetService<ILoggerFactory>();

            services.AddDbContextPool<VehicleDbContext>(options => options.UseSqlServer(
           _systemLocalConfiguration.EventDbConnection,
               //enable connection resilience
               connectOptions =>
               {
                   connectOptions.EnableRetryOnFailure();
                   connectOptions.CommandTimeout(Identifiers.TimeoutInSec);
               })//.UseLoggerFactory(loggerFactorySrv)// to log queries
             );
            //add application insights information, could be used to monitor the performance, and more analytics when application moved to the cloud.
            loggerFactorySrv.AddApplicationInsights(services.BuildServiceProvider(), LogLevel.Information);

            ILogger _logger = loggerFactorySrv
                .AddConsole()
                .AddDebug()
                .AddFile(Configuration.GetSection("Logging"))
                .CreateLogger<Startup>();

            // no need to inject the following service since, currently they are injected for the mediator.

            services.AddSingleton<MiddlewareConfiguration, MiddlewareConfiguration>(srv => _systemLocalConfiguration);
            services.AddScoped<IOperationalUnit, IOperationalUnit>(srv => new OperationalUnit(
                environment: Environemnt.EnvironmentName,
                assembly: AssemblyName));
            services.AddScoped<IMessageCommand, RabbitMQPublisher>(srv => new RabbitMQPublisher(loggerFactorySrv,
            new RabbitMQConfiguration
            {
                hostName = _systemLocalConfiguration.MessagesMiddleware,
                exchange = _systemLocalConfiguration.MiddlewareExchange,
                userName = _systemLocalConfiguration.MessagesMiddlewareUsername,
                password = _systemLocalConfiguration.MessagesMiddlewarePassword,
                routes = new string[] { _systemLocalConfiguration.MessagePublisherRoute }
            }));
            services.AddOptions();

            #region worker

            #region vehicle worker

            services.AddSingleton<IHostedService, RabbitMQSubscriberWorker>(srv =>
            {
                //get Vehicle service
                var vehicleSrv = new VehicleManager(loggerFactorySrv, srv.GetService<VehicleDbContext>());
                var cacheSrv = new CacheManager(Logger, _systemLocalConfiguration.CacheServer);
                return new RabbitMQSubscriberWorker
                (serviceProvider, loggerFactorySrv, new RabbitMQConfiguration
                {
                    hostName = _systemLocalConfiguration.MessagesMiddleware,
                    exchange = _systemLocalConfiguration.MiddlewareExchange,
                    userName = _systemLocalConfiguration.MessagesMiddlewareUsername,
                    password = _systemLocalConfiguration.MessagesMiddlewarePassword,
                    routes = _systemLocalConfiguration.MessageSubscriberRoute?.Split('-') ?? new string[0]
                }
                    , (messageCallback) =>
                    {
                        try
                        {
                            var message = messageCallback();
                            if (message != null)
                            {
                                var domainModel = Utilities.JsonBinaryDeserialize<VehicleModel>(message);
                                var vehicle = new VehicleSQLDB.DbModels.Vehicle(domainModel.Body);
                                //get the correlated customer from the cache, to fill name field
                                var customerBinary = cacheSrv.GetBinary(vehicle.CustomerId.ToString())?.Result;
                                if (customerBinary != null)
                                {
                                    var customer = Utilities.JsonBinaryDeserialize<Customer>(customerBinary);
                                    vehicle.CustomerName = customer.Name;
                                }
                                vehicleSrv.Add(vehicle).Wait();
                                cacheSrv.SetBinary(vehicle.ChassisNumber, Utilities.JsonBinarySerialize(vehicle)).Wait();
                            }
                            Logger.LogInformation($"[x] Vehicle service receiving a message from exchange: {_systemLocalConfiguration.MiddlewareExchange}, route :{_systemLocalConfiguration.MessageSubscriberRoute}");
                        }
                        catch (System.Exception ex)
                        {
                            Logger.LogCritical(ex, "Object de-serialization exception.");
                        }
                    });
            });

            #endregion

            #region tracking vehicle query client

            services.AddScoped<IMessageRequest<VehicleFilterModel, IEnumerable<DomainModels.Business.VehicleDomain.Vehicle>>,
            RabbitMQRequestClient<VehicleFilterModel, IEnumerable<DomainModels.Business.VehicleDomain.Vehicle>>>(
                srv =>
                {
                    return new RabbitMQRequestClient<VehicleFilterModel, IEnumerable<DomainModels.Business.VehicleDomain.Vehicle>>
                            (loggerFactorySrv, new RabbitMQConfiguration
                            {
                                exchange = "",
                                hostName = _systemLocalConfiguration.MessagesMiddleware,
                                userName = _systemLocalConfiguration.MessagesMiddlewareUsername,
                                password = _systemLocalConfiguration.MessagesMiddlewarePassword,
                                routes = new string[] { "rpc_queue_vehicle_filter" },
                            });
                });

            #endregion

            #region vehicle query worker
            // business logic
            services.AddSingleton<IHostedService, RabbitMQRequestWorker>(srv =>
            {
                var customerSrv = new VehicleManager(loggerFactorySrv, srv.GetService<VehicleDbContext>());

                return new RabbitMQRequestWorker
                (serviceProvider, loggerFactorySrv, new RabbitMQConfiguration
                {
                    exchange = "",
                    hostName = _systemLocalConfiguration.MessagesMiddleware,
                    userName = _systemLocalConfiguration.MessagesMiddlewareUsername,
                    password = _systemLocalConfiguration.MessagesMiddlewarePassword,
                    routes = new string[] { "rpc_queue_vehicle_filter" },
                }
                , (customerFilterMessageRequest) =>
                {
                    try
                    {
                        //TODO: add business logic, result should be serializable
                        var customerFilter = Utilities.JsonBinaryDeserialize<VehicleFilterModel>(customerFilterMessageRequest);
                        Logger.LogInformation($"[x] callback of RabbitMQ customer worker=> a message");
                        var response = customerSrv.Query((c) =>
                        {
                            return c.CustomerId == customerFilter.Body?.CustomerId;
                        })?.ToList();
                        if (response == null)
                            return new byte[0];
                        return Utilities.JsonBinarySerialize(response);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogCritical(ex, "Object de-serialization exception.");
                        //to respond back to RPC client
                        return new byte[0];
                    }
                });
            });
            #endregion

            #endregion

            ///
            /// Injecting message receiver background service
            ///

            services.AddDistributedRedisCache(redisOptions =>
            {
                redisOptions.Configuration = _systemLocalConfiguration.CacheServer;
                redisOptions.Configuration = _systemLocalConfiguration.VehiclesCacheDB;
            });

            services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new Microsoft.AspNetCore.Mvc.ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
            });

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new Info { Title = AssemblyName, Version = "v1" });
            });

            services.AddMediatR();

            var _operationalUnit = new OperationalUnit(
              environment: Environemnt.EnvironmentName,
              assembly: AssemblyName);

            services.AddMvc(options =>
            {
                //TODO: add practical policy instead of empty policy for authentication / authorization .
                options.Filters.Add(new CustomAuthorizer(_logger, _operationalUnit));
                options.Filters.Add(new CustomeExceptoinHandler(_logger, _operationalUnit, Environemnt));
                options.Filters.Add(new CustomResponseResult(_logger, _operationalUnit));
            });

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IDistributedCache cache, IHostingEnvironment environemnt)
        {
            // initialize InfoDbContext
            using (var scope = app.ApplicationServices.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetService<VehicleDbContext>();
                dbContext?.Database?.EnsureCreated();
            }
            app.UseStatusCodePages();
            if (environemnt.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }
            // Enable static files (if exists)
            app.UseStaticFiles();
            // Enable middleware to serve generated Swagger as a JSON endpoint.
            app.UseSwagger();
            // Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.), specifying the Swagger JSON endpoint.
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", AssemblyName);
            });
            app.UseMvc();
        }
    }
}
