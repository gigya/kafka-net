﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using KafkaClient.Common;
using KafkaClient.Connection;
using KafkaClient.Protocol;

namespace KafkaClient
{
    /// <summary>
    /// This provider blocks while it attempts to get the Metadata configuration of the Kafka servers.  If any retry errors occurs it will
    /// continue to block the downstream call and then repeatedly query kafka until the retry errors subside.  This repeat call happens in
    /// a backoff manner, which each subsequent call waiting longer before a requery.
    ///
    /// Error Codes:
    /// LeaderNotAvailable = 5
    /// NotLeaderForPartition = 6
    /// ConsumerCoordinatorNotAvailable = 15
    /// BrokerId = -1
    ///
    /// Documentation:
    /// https://cwiki.apache.org/confluence/display/KAFKA/A+Guide+To+The+Kafka+Protocol#AGuideToTheKafkaProtocol-MetadataResponse
    /// </summary>
    public class KafkaMetadataProvider : IDisposable
    {
        private const int BackoffMilliseconds = 100;

        private readonly ILog _log;

        private bool _interrupted;

        public KafkaMetadataProvider(ILog log)
        {
            _log = log;
        }

        /// <summary>
        /// Given a collection of server connections, query for the topic metadata.
        /// </summary>
        /// <param name="connections">The server connections to query.  Will cycle through the collection, starting at zero until a response is received.</param>
        /// <param name="topics">The collection of topics to get metadata for.</param>
        /// <param name="cancellationToken">Token for cancelling async action.</param>
        /// <returns>MetadataResponse validated to be complete.</returns>
        public Task<MetadataResponse> GetAsync(IEnumerable<IConnection> connections, IEnumerable<string> topics, CancellationToken cancellationToken)
        {
            var request = new MetadataRequest(topics);
            if (request.Topics.Count <= 0) return null;
            return GetAsync(connections, request, cancellationToken);
        }

        /// <summary>
        /// Given a collection of server connections, query for all topics metadata.
        /// </summary>
        /// <param name="connections">The server connections to query.  Will cycle through the collection, starting at zero until a response is received.</param>
        /// <param name="cancellationToken">Token for cancelling async action.</param>
        /// <returns>MetadataResponse validated to be complete.</returns>
        public Task<MetadataResponse> GetAsync(IEnumerable<IConnection> connections, CancellationToken cancellationToken)
        {
            var request = new MetadataRequest();
            return GetAsync(connections, request, cancellationToken);
        }

        private async Task<MetadataResponse> GetAsync(IEnumerable<IConnection> connections, MetadataRequest request, CancellationToken cancellationToken)
        {
            const int maxRetryAttempt = 2;
            bool performRetry;
            var retryAttempt = 0;
            MetadataResponse metadataResponse;

            var connectionList = ImmutableList<IConnection>.Empty.AddNotNullRange(connections);
            do {
                performRetry = false;
                metadataResponse = await GetMetadataResponseAsync(connectionList, request, cancellationToken);
                if (metadataResponse == null) return null;

                foreach (var validation in ValidateResponse(metadataResponse)) {
                    switch (validation.Status) {
                        case ValidationResult.Retry:
                            performRetry = true;
                            _log.WarnFormat(validation.Message);
                            break;

                        case ValidationResult.Error:
                            if (validation.ErrorCode == ErrorResponseCode.NoError) throw new ConnectionException(validation.Message);

                            throw new RequestException(request.ApiKey, validation.ErrorCode, validation.Message);
                    }
                }

                await BackoffOnRetryAsync(++retryAttempt, performRetry, cancellationToken).ConfigureAwait(false);
            } while (retryAttempt < maxRetryAttempt && _interrupted == false && performRetry);

            return metadataResponse;
        }

        private async Task BackoffOnRetryAsync(int retryAttempt, bool performRetry, CancellationToken cancellationToken)
        {
            if (performRetry && retryAttempt > 0)
            {
                var backoff = retryAttempt * retryAttempt * BackoffMilliseconds;
                _log.WarnFormat("Backing off metadata request retry.  Waiting for {0}ms.", backoff);
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<MetadataResponse> GetMetadataResponseAsync(IEnumerable<IConnection> connections, MetadataRequest request, CancellationToken cancellationToken)
        {
            var servers = "";
            foreach (var conn in connections) {
                try {
                    servers += " " + conn.Endpoint;
                    return await conn.SendAsync(request, cancellationToken).ConfigureAwait(false);
                } catch (Exception ex) {
                    _log.WarnFormat(ex, "Failed to contact {0}. Trying next server", conn.Endpoint);
                }
            }

            throw new RequestException(request.ApiKey, ErrorResponseCode.NoError, $"Unable to query for metadata from any of the provided Kafka servers {servers}");
        }

        private IEnumerable<MetadataValidationResult> ValidateResponse(MetadataResponse metadata)
        {
            foreach (var broker in metadata.Brokers) {
                yield return ValidateBroker(broker);
            }
            foreach (var topic in metadata.Topics) {
                yield return ValidateTopic(topic);
            }
        }

        private MetadataValidationResult ValidateBroker(Broker broker)
        {
            if (broker.BrokerId == -1) {
                return new MetadataValidationResult { Status = ValidationResult.Retry, ErrorCode = ErrorResponseCode.Unknown };
            }

            if (string.IsNullOrEmpty(broker.Host)) {
                return new MetadataValidationResult {
                    Status = ValidationResult.Error,
                    ErrorCode = ErrorResponseCode.NoError,
                    Message = "Broker missing host information."
                };
            }

            if (broker.Port <= 0) {
                return new MetadataValidationResult {
                    Status = ValidationResult.Error,
                    ErrorCode = ErrorResponseCode.NoError,
                    Message = "Broker missing port information."
                };
            }

            return new MetadataValidationResult();
        }

        private MetadataValidationResult ValidateTopic(MetadataTopic topic)
        {
            var errorCode = topic.ErrorCode;
            try
            {
                if (errorCode == ErrorResponseCode.NoError) return new MetadataValidationResult();

                switch (errorCode)
                {
                    case ErrorResponseCode.LeaderNotAvailable:
                    case ErrorResponseCode.OffsetsLoadInProgress:
                    case ErrorResponseCode.ConsumerCoordinatorNotAvailable:
                        return new MetadataValidationResult {
                            Status = ValidationResult.Retry,
                            ErrorCode = errorCode,
                            Message = $"topic/{topic.TopicName} returned error code of {errorCode}: Retrying"
                        };
                }

                return new MetadataValidationResult {
                    Status = ValidationResult.Error,
                    ErrorCode = errorCode,
                    Message = $"topic/{topic.TopicName} returned an error of {errorCode}"
                };
            }
            catch
            {
                return new MetadataValidationResult
                {
                    Status = ValidationResult.Error,
                    ErrorCode = ErrorResponseCode.Unknown,
                    Message = $"topic/{topic.TopicName} returned unknown error of {errorCode} in metadata response"
                };
            }
        }

        public void Dispose()
        {
            _interrupted = true;
        }
    }

    public enum ValidationResult
    {
        Valid,
        Error,
        Retry
    }

    public class MetadataValidationResult
    {
        public ValidationResult Status { get; set; }
        public string Message { get; set; }
        public ErrorResponseCode ErrorCode { get; set; }

        public MetadataValidationResult()
        {
            ErrorCode = ErrorResponseCode.NoError;
            Status = ValidationResult.Valid;
            Message = "";
        }
    }
}