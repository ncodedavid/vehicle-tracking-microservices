﻿using BuildingAspects.Behaviors;
using BuildingAspects.Utilities;
using DomainModels.DataStructure;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BackgroundMiddleware
{
    public class RabbitMQRequestWorker : BackgroundService, IDisposable
    {
        private const int defaultMiddlewarePort = 5672;//default rabbitmq port
        private readonly ILogger _logger;
        private readonly RabbitMQConfiguration _hostConfig;
        private readonly IConnectionFactory connectionFactory;
        private IConnection connection;
        private IModel channel;
        private readonly EventingBasicConsumer consumer;
        public readonly string exchange, route;
        private readonly Func<byte[], byte[]> lambda;
        /// <summary>
        /// internal construct subscriber object
        /// </summary>
        /// <param name="logger">ILogger instance</param>
        /// <param name="hostConfig">rabbitMQ configuration</param>
        public RabbitMQRequestWorker(
            IServiceProvider serviceProvider,
            ILoggerFactory logger,
            RabbitMQConfiguration hostConfig,
            Func<byte[], byte[]> lambda) : base(serviceProvider)
        {

            _logger = logger?
                            .AddConsole()
                            .AddDebug()
                            .CreateLogger<RabbitMQPublisher>()
                            ?? throw new ArgumentNullException("Logger reference is required");
            try
            {
                if (string.IsNullOrEmpty(hostConfig.hostName))
                    throw new ArgumentNullException("hostName is invalid");
                _hostConfig = hostConfig;
                exchange = _hostConfig.exchange;
                route = _hostConfig.routes.FirstOrDefault() ?? throw new ArgumentNullException("route queue is missing.");
                this.lambda = lambda ?? throw new ArgumentNullException("Callback reference is invalid");
                var host = Helper.ExtractHostStructure(_hostConfig.hostName);
                connectionFactory = new ConnectionFactory()
                {
                    HostName = host.hostName,
                    Port = host.port ?? defaultMiddlewarePort,
                    UserName = _hostConfig.userName,
                    Password = _hostConfig.password,
                    ContinuationTimeout = TimeSpan.FromSeconds(DomainModels.System.Identifiers.TimeoutInSec)
                };

                new Function(_logger, DomainModels.System.Identifiers.RetryCount).Decorate(() =>
                {
                    connection = connectionFactory.CreateConnection();
                    channel = connection.CreateModel();
                    channel.QueueDeclare(queue: route, durable: false, exclusive: false, autoDelete: false, arguments: null);
                    //TODO: in case scaling the middleware, running multiple workers simultaneously. 
                    channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
                    return Task.CompletedTask;
                }, (ex) =>
                {
                    switch (ex)
                    {
                        case BrokerUnreachableException brokerEx:
                            return true;
                        case ConnectFailureException connEx:
                            return true;
                        case SocketException socketEx:
                            return true;
                        default:
                            return false;
                    }
                }).Wait();

                consumer = new EventingBasicConsumer(channel);
                consumer.Received += async (model, ea) =>
                {
                    await new Function(_logger, DomainModels.System.Identifiers.RetryCount).Decorate(() =>
                    {
                        var props = ea.BasicProperties;
                        var replyProps = channel.CreateBasicProperties();
                        replyProps.CorrelationId = props.CorrelationId;
                        if (ea.Body == null || ea.Body.Length == 0)
                            throw new TypeLoadException("Invalid message type");
                        // callback action feeding  
                        var binaryResponse = lambda(ea.Body);
                        channel.BasicPublish(exchange: exchange, routingKey: props.ReplyTo, basicProperties: replyProps, body: binaryResponse);
                        channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);

                        _logger.LogInformation($"[x] Event sourcing service receiving a messaged from exchange: {_hostConfig.exchange}, route :{ea.RoutingKey}.");
                        return true;
                    }, (ex) =>
                    {
                        switch (ex)
                        {
                            case TypeLoadException typeEx:
                                return true;
                            default:
                                return false;
                        }
                    });
                };
                //bind event handler
                channel.BasicConsume(queue: route, autoAck: false, consumer: consumer);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize RabbitMQQueryWorker", ex);
                throw ex;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {

            _logger.LogInformation(" [x] Awaiting RPC requests");
            //bind event handler
            Console.ReadLine();
            return Task.CompletedTask;
        }
        public override void Dispose()
        {
            if (connection != null && connection.IsOpen)
                connection.Close();
            base.Dispose();
        }
    }
}
