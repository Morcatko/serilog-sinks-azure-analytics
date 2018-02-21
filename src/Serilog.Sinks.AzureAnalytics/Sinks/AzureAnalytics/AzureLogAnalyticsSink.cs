﻿// Copyright 2018 Zethian Inc.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.AzureAnalytics;
using Serilog.Sinks.Batch;
using Serilog.Sinks.Extensions;

namespace Serilog.Sinks
{
    internal class AzureLogAnalyticsSink : BatchProvider, ILogEventSink
    {
        private static readonly HttpClient _httpClient;
        private static readonly MediaTypeHeaderValue _contentType;
        private readonly Uri _analyticsUrl;
        private readonly string _authenticationId;
        private readonly IFormatProvider _formatProvider;
        private readonly string _logName;
        private readonly bool _storeTimestampInUtc;
        private readonly string _workSpaceId;

        static AzureLogAnalyticsSink()
        {
            _httpClient = new HttpClient();
            _contentType = MediaTypeHeaderValue.Parse("application/json");
        }

        internal AzureLogAnalyticsSink(
            string workSpaceId,
            string authenticationId,
            string logName,
            bool storeTimestampInUtc,
            IFormatProvider formatProvider,
            int logBufferSize = 25_000,
            int batchSize = 100,
            AzureOfferingType azureOfferingType = AzureOfferingType.Public) : base(batchSize, logBufferSize)
        {
            _workSpaceId = workSpaceId;
            _authenticationId = authenticationId;
            _logName = logName;
            _storeTimestampInUtc = storeTimestampInUtc;
            _formatProvider = formatProvider;

            var urlSuffix = azureOfferingType == AzureOfferingType.US_Government ? ".us" : ".com";
            _analyticsUrl =
                new Uri(
                    $"https://{_workSpaceId}.ods.opinsights.azure{urlSuffix}/api/logs?api-version=2016-04-01");
        }

        #region ILogEvent implementation

        public void Emit(LogEvent logEvent)
        {
            PushEvent(logEvent);
        }

        #endregion

        protected override async Task<bool> WriteLogEventAsync(ICollection<LogEvent> logEventsBatch)
        {
            if ((logEventsBatch == null) || (logEventsBatch.Count == 0))
                return true;

            var logEventJsonBuilder = new StringBuilder();

            foreach (var logEvent in logEventsBatch)
            {
                var jsonString = JsonConvert.SerializeObject(
                    JObject.FromObject(
                            logEvent.Dictionary(
                                _storeTimestampInUtc,
                                _formatProvider))
                        .Flaten());

                logEventJsonBuilder.Append(jsonString);
                logEventJsonBuilder.Append(",");
            }

            if (logEventJsonBuilder.Length > 0)
                logEventJsonBuilder.Remove(logEventJsonBuilder.Length - 1, 1);

            if (logEventsBatch.Count > 1)
            {
                logEventJsonBuilder.Insert(0, "[");
                logEventJsonBuilder.Append("]");
            }

            var logEventJsonString = logEventJsonBuilder.ToString();
            var contentLength = Encoding.UTF8.GetByteCount(logEventJsonString);

            var dateString = DateTime.UtcNow.ToString("r");
            var hashedString = BuildSignature(contentLength, dateString, _authenticationId);
            var signature = $"SharedKey {_workSpaceId}:{hashedString}";

            var result = await PostDataAsync(signature, dateString, logEventJsonString)
                .ConfigureAwait(true);
            return result == "OK";
        }

        private static string BuildSignature(int contentLength, string dateString, string key)
        {
            var stringToHash =
                "POST\n" +
                contentLength +
                "\napplication/json\n" +
                "x-ms-date:" + dateString +
                "\n/api/logs";

            var encoding = new UTF8Encoding();
            var keyByte = Convert.FromBase64String(key);
            var messageBytes = encoding.GetBytes(stringToHash);
            using (var hmacsha256 = new HMACSHA256(keyByte))
            {
                return Convert.ToBase64String(hmacsha256.ComputeHash(messageBytes));
            }
        }

        private async Task<string> PostDataAsync(string signature, string dateString, string jsonString)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _analyticsUrl))
                {
                    request.Headers.Add("Authorization", signature);
                    request.Headers.Add("x-ms-date", dateString);
                    request.Content = new StringContent(jsonString);
                    request.Content.Headers.ContentType = _contentType;
                    request.Headers.Add("Log-Type", _logName);

                    using (var response = await _httpClient.SendAsync(request))
                    {
                        var message = await response.Content.ReadAsStringAsync()
                            .ConfigureAwait(false);

                        SelfLog.WriteLine("{0}: {1}", response.ReasonPhrase, message);
                        return response.ReasonPhrase;
                    }
                }
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("ERROR: " + (ex.InnerException ?? ex).Message);
                return "FAILED";
            }
        }
    }
}