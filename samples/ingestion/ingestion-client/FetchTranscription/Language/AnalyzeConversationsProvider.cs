// <copyright file="AnalyzeConversationsProvider.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
// </copyright>

namespace Language
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Azure;
    using Azure.AI.Language.Conversations;
    using Azure.Core;
    using Connector;
    using Connector.Serializable.Language.Conversations;
    using Connector.Serializable.TranscriptionStartedServiceBusMessage;
    using FetchTranscriptionFunction;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;

    using static Connector.Serializable.TranscriptionStartedServiceBusMessage.TextAnalyticsRequest;

    /// <summary>
    /// Analyze Conversations async client.
    /// </summary>
    public class AnalyzeConversationsProvider
    {
        private const string DefaultInferenceSource = "lexical";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
        private readonly ConversationAnalysisClient ConversationAnalysisClient;
        private readonly string Locale;
        private readonly ILogger Log;

        public AnalyzeConversationsProvider(string locale, string subscriptionKey, string region, ILogger log)
        {
            ConversationAnalysisClient = new ConversationAnalysisClient(new Uri($"https://{region}.api.cognitive.microsoft.com"), new AzureKeyCredential(subscriptionKey));

            Locale = locale;
            Log = log;
        }

        public static bool IsConversationalPiiEnabled()
        {
            return FetchTranscriptionEnvironmentVariables.ConversationPiiSetting != Connector.Enums.ConversationPiiSetting.None;
        }

        /// <summary>
        /// API to submit an analyzeConversations async Request.
        /// </summary>
        /// <param name="speechTranscript">Instance of the speech transcript.</param>
        /// <returns>An enumerable of the jobs IDs and errors if any.</returns>
        public async Task<(IEnumerable<string> jobIds, IEnumerable<string> errors)> SubmitAnalyzeConversationsRequestAsync(SpeechTranscript speechTranscript)
        {
            speechTranscript = speechTranscript ?? throw new ArgumentNullException(nameof(speechTranscript));

            if (!IsConversationalPiiEnabled())
            {
                return (new List<string>(), new List<string>());
            }

            var data = new List<AnalyzeConversationsRequest>();
            var previousTextElementCount = -1;
            var jobCount = 0;
            var turnCount = 0;
            foreach (var recognizedPhrase in speechTranscript.RecognizedPhrases
                .Where(rp => rp.NBest.FirstOrDefault() != null && !string.IsNullOrEmpty(rp.NBest.First().Display)))
            {
                var topResult = recognizedPhrase.NBest.First();
                var textElementCount = GetInputSize(topResult, FetchTranscriptionEnvironmentVariables.ConversationPiiInferenceSource);

                // We do not support cases where the content size of recognized phrases to be greater than 5000 character chunks as this would mean we need to chunk the conversation turn and our model performance degrades if the content of a recognized phrase is chunked.
                // We will add an error in this case. We can add logic to handle this further.
                if (textElementCount > FetchTranscriptionEnvironmentVariables.ConversationPiiMaxChunkSize)
                {
                    var errors = new List<string> { $"The conversation contains a recognized phrase [offset : ${recognizedPhrase.Offset} channel: ${recognizedPhrase.Channel}] where the size if greater than {FetchTranscriptionEnvironmentVariables.ConversationPiiMaxChunkSize} Text Elements. Ignoring the conversation." };

                    return (null, errors);
                }

                if (previousTextElementCount == -1 || (previousTextElementCount + textElementCount) > FetchTranscriptionEnvironmentVariables.ConversationPiiMaxChunkSize)
                {
                    // create a new job
                    previousTextElementCount = 0;
                    jobCount++;

                    data.Add(new AnalyzeConversationsRequest
                    {
                        DisplayName = "IngestionClient",
                        AnalysisInput = new AnalysisInput(new[]
                        {
                            new Conversation
                            {
                                Id = $"{jobCount}",
                                Language = Locale,
                                Modality = Modality.transcript,
                                ConversationItems = new List<ConversationItem>()
                            }
                        }),
                        Tasks = new[]
                        {
                            new AnalyzeConversationsTask
                            {
                                TaskName = "Conversation PII task",
                                Kind = AnalyzeConversationsTaskKind.ConversationalPIITask,
                                Parameters = new Dictionary<string, object>
                                {
                                    {
                                        "piiCategories", FetchTranscriptionEnvironmentVariables.ConversationPiiCategories.ToList()
                                    },
                                    {
                                        "redactionSource", FetchTranscriptionEnvironmentVariables.ConversationPiiInferenceSource ?? DefaultInferenceSource
                                    },
                                    {
                                        "includeAudioRedaction", FetchTranscriptionEnvironmentVariables.ConversationPiiSetting == Connector.Enums.ConversationPiiSetting.IncludeAudioRedaction
                                    }
                                }
                            }
                        }
                    });
                }

                data.Last().AnalysisInput.Conversations[0].ConversationItems.Add(new ConversationItem
                {
                    Text = topResult.Display,
                    Lexical = topResult.Lexical,
                    Itn = topResult.ITN,
                    MaskedItn = topResult.MaskedITN,
                    Id = $"{turnCount}__{recognizedPhrase.Offset}__{recognizedPhrase.Channel}",
                    ParticipantId = $"{recognizedPhrase.Channel}",
                    AudioTimings = topResult.Words
                        ?.Select(word => new WordLevelAudioTiming
                        {
                            Word = word.Word,
                            Duration = (long)word.DurationInTicks,
                            Offset = (long)word.OffsetInTicks
                        })
                });
                previousTextElementCount += textElementCount;
                turnCount++;
            }

            Log.LogInformation($"Submitting {jobCount} jobs to Conversations...");

            return await SubmitConversationsAsync(data).ConfigureAwait(false);
        }

        /// <summary>
        /// API to get the job result of all analyze conversation jobs.
        /// </summary>
        /// <param name="jobIds">Enumerable of conversational jobIds.</param>
        /// <returns>Enumerable of results of conversation PII redaction and errors encountered if any.</returns>
        public async Task<(AnalyzeConversationPiiResults piiResults, IEnumerable<string> errors)> GetConversationsOperationsResult(IEnumerable<string> jobIds)
        {
            var errors = new List<string>();
            var piiResults = new AnalyzeConversationPiiResults();
            if (!jobIds.Any())
            {
                return (null, errors);
            }

            try
            {
                var tasks = jobIds.Select(async jobId => await GetConversationsOperationResults(jobId).ConfigureAwait(false));
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);

                var piiErrors = results.SelectMany(result => result.piiResults).SelectMany(s => s.Errors);
                if (piiErrors.Any())
                {
                    errors.AddRange(piiErrors.Select(s => $"Error thrown for conversation : {s.Id} message: [{s.Error.Code}: {s.Error.Message}]"));
                    return (null, errors);
                }

                var warnings = results.SelectMany(result => result.piiResults).SelectMany(s => s.Conversations).SelectMany(s => s.Warnings);
                var conversationItems = results.SelectMany(result => result.piiResults).SelectMany(s => s.Conversations).SelectMany(s => s.ConversationItems);

                var combinedRedactedContent = new List<CombinedConversationPiiResult>();

                foreach (var group in conversationItems.GroupBy(item => item.Channel))
                {
#pragma warning disable CA1305 // Specify IFormatProvider
                    var items = group.ToList().OrderBy(s => int.Parse(s.Id));
#pragma warning restore CA1305 // Specify IFormatProvider

                    combinedRedactedContent.Add(new CombinedConversationPiiResult
                    {
                        Channel = group.Key,
                        Display = string.Join(" ", group.Select(s => s.RedactedContent.Text)).Trim(),
                        ITN = string.Join(" ", group.Select(s => s.RedactedContent.Itn)).Trim(),
                        Lexical = string.Join(" ", group.Select(s => s.RedactedContent.Lexical)).Trim(),
                    });
                }

                piiResults.Conversations = new List<ConversationPiiResult>
                {
                    new ConversationPiiResult
                    {
                        Warnings = warnings,
                        ConversationItems = conversationItems
                    }
                };
                piiResults.CombinedRedactedContent = combinedRedactedContent;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                errors.Add($"Exception when parsing result from TA: {ex.Message}");
            }

            return (piiResults, errors);
        }

        /// <summary>
        /// Checks for all conversational analytics requests that were marked as running if they have completed and sets a new state accordingly.
        /// </summary>
        /// <param name="audioFileInfos">Enumerable for audioFiles.</param>
        /// <returns>True if all requests completed, else false.</returns>
        public async Task<bool> ConversationalRequestsCompleted(IEnumerable<AudioFileInfo> audioFileInfos)
        {
            if (!IsConversationalPiiEnabled() || !audioFileInfos.Where(audioFileInfo => audioFileInfo.TextAnalyticsRequests?.ConversationRequests != null).Any())
            {
                return true;
            }

            var conversationRequests = audioFileInfos
                    .Where(audioFileInfo => audioFileInfo.TextAnalyticsRequests?.ConversationRequests != null)
                    .SelectMany(audioFileInfo => audioFileInfo.TextAnalyticsRequests.ConversationRequests)
                    .Where(text => text.Status == TextAnalyticsRequestStatus.Running);

            var runningJobsCount = 0;

            foreach (var textAnalyticsJob in conversationRequests)
            {
                var response = await ConversationAnalysisClient.GetAnalyzeConversationJobStatusAsync(Guid.Parse(textAnalyticsJob.Id)).ConfigureAwait(false);

                if (response.IsError)
                {
                    continue;
                }

                var analysisResult = JsonConvert.DeserializeObject<AnalyzeConversationsResult>(response.Content.ToString());

                if (analysisResult.Tasks.InProgress != 0)
                {
                    // some jobs are still running.
                    runningJobsCount++;
                }
            }

            return runningJobsCount == 0;
        }

        /// <summary>
        /// Gets the (audio-level) results from text analytics, adds the results to the speech transcript.
        /// </summary>
        /// <param name="conversationJobIds">The conversation analysis job Ids.</param>
        /// <param name="speechTranscript">The speech transcript object.</param>
        /// <returns>The errors, if any.</returns>
        public async Task<IEnumerable<string>> AddConversationalEntitiesAsync(
            IEnumerable<string> conversationJobIds,
            SpeechTranscript speechTranscript)
        {
            speechTranscript = speechTranscript ?? throw new ArgumentNullException(nameof(speechTranscript));
            var errors = new List<string>();

            var isConversationalPiiEnabled = IsConversationalPiiEnabled();
            if (!isConversationalPiiEnabled)
            {
                return new List<string>();
            }

            if (conversationJobIds == null || !conversationJobIds.Any())
            {
                return errors;
            }

            var conversationsPiiResults = await GetConversationsOperationsResult(conversationJobIds).ConfigureAwait(false);

            if (conversationsPiiResults.errors.Any())
            {
                errors.AddRange(conversationsPiiResults.errors);
            }

            speechTranscript.ConversationAnalyticsResults = new ConversationAnalyticsResults
            {
                AnalyzeConversationPiiResults = conversationsPiiResults.piiResults,
            };

            return errors;
        }

        private static int GetInputSize(NBest topResult, string conversationPiiInferenceSource)
        {
            var inputString = topResult.Lexical;
            if (conversationPiiInferenceSource.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                inputString = topResult.Display;
            }
            else if (conversationPiiInferenceSource.Equals("itn", StringComparison.OrdinalIgnoreCase))
            {
                inputString = topResult.ITN;
            }
            else if (conversationPiiInferenceSource.Equals("maskedITN", StringComparison.OrdinalIgnoreCase))
            {
                inputString = topResult.MaskedITN;
            }

            var stringInfo = new StringInfo(inputString);
            return stringInfo.LengthInTextElements;
        }

        private async Task<(IEnumerable<string> jobId, IEnumerable<string> errors)> SubmitConversationsAsync(IEnumerable<AnalyzeConversationsRequest> data)
        {
            var errors = new List<string>();
            var jobs = new List<string>();
            try
            {
                Log.LogInformation($"Sending language conversation requests.");

                foreach (var request in data)
                {
                    using var input = RequestContent.Create(JsonConvert.SerializeObject(request));
                    var operation = await ConversationAnalysisClient.AnalyzeConversationAsync(WaitUntil.Started, input).ConfigureAwait(false);
                    operation.GetRawResponse().Headers.TryGetValue("operation-location", out var operationLocation);
                    var jobId = new Uri(operationLocation).AbsolutePath.Split("/").Last();
                    jobs.Add(jobId);
                }

                return (jobs, errors);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The operation has timed out after {RequestTimeout.TotalSeconds} seconds.");
            }

            // do not catch throttling errors, rather throw and retry
            catch (RequestFailedException e) when (e.Status != 429)
            {
                errors.Add($"Conversation analytics request failed with error: {e.Message}");
            }

            return (jobs, errors);
        }

        private async Task<(IEnumerable<AnalyzeConversationPiiResults> piiResults, IEnumerable<string> errors)> GetConversationsOperationResults(string jobId)
        {
            var errors = new List<string>();
            try
            {
                Log.LogInformation($"Sending conversation analytics request for jobid {jobId}.");

                var response = await ConversationAnalysisClient.GetAnalyzeConversationJobStatusAsync(Guid.Parse(jobId)).ConfigureAwait(false);

                if (response.IsError)
                {
                    errors.Add($"Conversation analysis failed with error.");
                }

                var analysisResult = JsonConvert.DeserializeObject<AnalyzeConversationsResult>(response.Content.ToString());

                if (!string.Equals(analysisResult.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    var errorMessages = analysisResult.Errors.Select(e => e.Error.Message);
                    errors.Add($"Conversation analysis request failed: {errorMessages.FirstOrDefault()}");
                    errors.AddRange(errorMessages);
                    return (null, errors);
                }

                if (analysisResult.Tasks.InProgress == 0)
                {
                    // all tasks completed.
                    return (analysisResult.Tasks
                        .Items.Where(item => item.Kind == AnalyzeConversationsTaskResultKind.conversationalPIIResults)
                        .Select(s => s as ConversationPiiItem)
                        .Select(s => s.Results), errors);
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The operation has timed out after {RequestTimeout.TotalSeconds} seconds.");
            }

            // do not catch throttling errors, rather throw and retry
            catch (RequestFailedException e) when (e.Status != 429)
            {
                errors.Add($"Conversation analysis request failed with error: {e.Message}");
            }

            Log.LogInformation($"Conversation analysis returned no result");
            return (null, errors);
        }
    }
}
