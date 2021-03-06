﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.ExternalApi;
using BuildXL.Ipc.Interfaces;
using BuildXL.Utilities;
using BuildXL.Utilities.Tracing;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.Content.Common.Authentication;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.Symbol.App.Core;
using Microsoft.VisualStudio.Services.Symbol.App.Core.Telemetry;
using Microsoft.VisualStudio.Services.Symbol.App.Core.Tracing;
using Microsoft.VisualStudio.Services.Symbol.Common;
using Microsoft.VisualStudio.Services.Symbol.WebApi;
using Newtonsoft.Json;
using Tool.ServicePipDaemon;
using static BuildXL.Utilities.FormattableStringEx;

namespace Tool.SymbolDaemon
{
    /// <summary>
    /// Client for Artifact Services symbols endpoint.
    /// </summary>
    public sealed class VsoSymbolClient : ISymbolClient
    {
        private static IAppTraceSource Tracer => SymbolAppTraceSource.SingleInstance;

        private readonly Client m_apiClient;

        private readonly IIpcLogger m_logger;
        private readonly SymbolConfig m_config;
        private readonly ISymbolServiceClient m_symbolClient;
        private readonly CancellationTokenSource m_cancellationSource;
        private readonly DebugEntryCreateBehavior m_debugEntryCreateBehavior;

        private CancellationToken CancellationToken => m_cancellationSource.Token;
        private string m_requestId;
        private IDomainId m_domainId;

        private VssCredentials GetCredentials() =>
            new VsoCredentialHelper(m => m_logger.Verbose(m))
                .GetCredentials(m_config.Service, true, null, null, PromptBehavior.Never);

        private ArtifactHttpClientFactory GetFactory() =>
            new ArtifactHttpClientFactory(
                credentials: GetCredentials(),
                httpSendTimeout: m_config.HttpSendTimeout,
                tracer: Tracer,
                verifyConnectionCancellationToken: CancellationToken);

        private Uri ServiceEndpoint => m_config.Service;

        private string RequestName => m_config.Name;

        private string RequestId
        {
            get
            {
                Contract.Requires(!string.IsNullOrEmpty(m_requestId));
                return m_requestId;
            }
        }

        private IDomainId DomainId
        {
            get
            {
                Contract.Requires(m_domainId != null);
                return m_domainId;
            }
        }

        private readonly CounterCollection<SymbolClientCounter> m_counters;

        /// <nodoc />
        public VsoSymbolClient(IIpcLogger logger, SymbolConfig config, Client apiClient)
        {
            m_logger = logger;
            m_apiClient = apiClient;
            m_config = config;
            m_debugEntryCreateBehavior = config.DebugEntryCreateBehavior;
            m_cancellationSource = new CancellationTokenSource();

            m_counters = new CounterCollection<SymbolClientCounter>();

            m_logger.Info(I($"[{nameof(VsoSymbolClient)}] Using symbol config: {JsonConvert.SerializeObject(m_config)}"));

            m_symbolClient = new ReloadingSymbolClient(
                logger: logger,
                clientConstructor: CreateSymbolServiceClient);
        }

        private ISymbolServiceClient CreateSymbolServiceClient()
        {
            using (m_counters.StartStopwatch(SymbolClientCounter.AuthDuration))
            {
                var client = new SymbolServiceClient(
                    ServiceEndpoint,
                    GetFactory(),
                    Tracer,
                    new SymbolServiceClientTelemetry(Tracer, ServiceEndpoint, enable: m_config.EnableTelemetry));

                return client;
            }
        }

        /// <summary>
        /// Queries the endpoint for the RequestId. 
        /// This method should be called only after the request has been created, otherwise, it will throw an exception.
        /// </summary>
        /// <remarks>
        /// On workers, m_requestId / m_domainId won't be initialized, so we need to query the server for the right values.
        /// </remarks>
        private async Task EnsureRequestIdAndDomainIdAreInitalizedAsync()
        {
            if (string.IsNullOrEmpty(m_requestId) || m_domainId == null)
            {
                using (m_counters.StartStopwatch(SymbolClientCounter.GetRequestIdDuration))
                {
                    var result = await m_symbolClient.GetRequestByNameAsync(RequestName, CancellationToken);
                    m_requestId = result.Id;
                    m_domainId = result.DomainId;
                }
            }
        }

        /// <summary>
        /// Creates a symbol request.
        /// </summary>
        public async Task<Request> CreateAsync(CancellationToken token)
        {
            if (!m_config.DomainId.HasValue)
            {
                m_logger.Verbose("DomainId is not specified. Creating symbol publishing request using DefaultDomainId.");
            }

            IDomainId domainId = m_config.DomainId.HasValue
                ? new ByteDomainId(m_config.DomainId.Value)
                : WellKnownDomainIds.DefaultDomainId;

            Request result;
            using (m_counters.StartStopwatch(SymbolClientCounter.CreateDuration))
            {
                result = await m_symbolClient.CreateRequestAsync(domainId, RequestName, m_config.EnableChunkDedup, token);
            }

            m_requestId = result.Id;
            m_domainId = result.DomainId;

            // info about a request in a human-readable form
            var requestDetails = $"Symbol request has been created:{Environment.NewLine}"
                + $"ID: {result.Id}{Environment.NewLine}"
                + $"Name: {result.Name}{Environment.NewLine}"
                + $"Content list: '{result.Url}/DebugEntries'";

            // Send the message to the main log.
            Analysis.IgnoreResult(await m_apiClient.LogMessage(requestDetails));

            m_logger.Verbose(requestDetails);

            return result;
        }

        /// <inheritdoc />
        public Task<Request> CreateAsync() => CreateAsync(CancellationToken);

        /// <inheritdoc />
        public async Task<AddDebugEntryResult> AddFileAsync(SymbolFile symbolFile)
        {
            Contract.Requires(symbolFile.IsIndexed, "File has not been indexed.");

            m_counters.IncrementCounter(SymbolClientCounter.NumAddFileRequests);

            if (symbolFile.DebugEntries.Count == 0)
            {
                // If there are no debug entries, ask bxl to log a message and return early.
                Analysis.IgnoreResult(await m_apiClient.LogMessage(I($"File '{symbolFile.FullFilePath}' does not contain symbols and will not be added to '{RequestName}'."), isWarning: false));
                m_counters.IncrementCounter(SymbolClientCounter.NumFilesWithoutDebugEntries);

                return AddDebugEntryResult.NoSymbolData;
            }

            await EnsureRequestIdAndDomainIdAreInitalizedAsync();

            List<DebugEntry> result;
            using (m_counters.StartStopwatch(SymbolClientCounter.TotalAssociateTime))
            {
                try
                {
                    result = await m_symbolClient.CreateRequestDebugEntriesAsync(
                        RequestId,
                        symbolFile.DebugEntries.Select(e => CreateDebugEntry(e, m_domainId)),
                        // First, we create debug entries with ThrowIfExists behavior not to silence the collision errors.
                        DebugEntryCreateBehavior.ThrowIfExists,
                        CancellationToken);
                }
                catch (DebugEntryExistsException)
                {
                    string message = $"[SymbolDaemon] File: '{symbolFile.FullFilePath}' caused collision. " +
                        (m_debugEntryCreateBehavior == DebugEntryCreateBehavior.ThrowIfExists
                            ? string.Empty
                            : $"SymbolDaemon will retry creating debug entry with {m_debugEntryCreateBehavior} behavior");

                    if (m_debugEntryCreateBehavior == DebugEntryCreateBehavior.ThrowIfExists)
                    {
                        // Log an error message in SymbolDaemon log file
                        m_logger.Error(message);
                        throw new DebugEntryExistsException(message);
                    }

                    // Log a message in SymbolDaemon log file
                    m_logger.Verbose(message);

                    result = await m_symbolClient.CreateRequestDebugEntriesAsync(
                        RequestId,
                        symbolFile.DebugEntries.Select(e => CreateDebugEntry(e, m_domainId)),
                        m_debugEntryCreateBehavior,
                        CancellationToken);
                }
            }

            var entriesWithMissingBlobs = result.Where(e => e.Status == DebugEntryStatus.BlobMissing).ToList();

            if (entriesWithMissingBlobs.Count > 0)
            {
                // All the entries are based on the same file, so we need to call upload only once.

                // make sure that the file is on disk (it might not be on disk if we got DebugEntries from cache/metadata file)
                var file = await symbolFile.EnsureMaterializedAsync();

                BlobIdentifierWithBlocks uploadResult;
                using (m_counters.StartStopwatch(SymbolClientCounter.TotalUploadTime))
                {
                    uploadResult = await m_symbolClient.UploadFileAsync(
                        m_domainId,
                        // uploading to the location set by the symbol service
                        entriesWithMissingBlobs[0].BlobUri,
                        RequestId,
                        symbolFile.FullFilePath,
                        entriesWithMissingBlobs[0].BlobIdentifier,
                        CancellationToken);
                }

                m_counters.IncrementCounter(SymbolClientCounter.NumFilesUploaded);
                m_counters.AddToCounter(SymbolClientCounter.TotalUploadSize, file.Length);

                m_logger.Info($"File: '{symbolFile.FullFilePath}' -- upload result: {uploadResult}");

                entriesWithMissingBlobs.ForEach(entry => entry.BlobDetails = uploadResult);

                using (m_counters.StartStopwatch(SymbolClientCounter.TotalAssociateAfterUploadTime))
                {
                    entriesWithMissingBlobs = await m_symbolClient.CreateRequestDebugEntriesAsync(
                        RequestId,
                        entriesWithMissingBlobs,
                        m_debugEntryCreateBehavior,
                        CancellationToken);
                }

                Contract.Assert(entriesWithMissingBlobs.All(e => e.Status != DebugEntryStatus.BlobMissing), "Entries with non-success code are present.");

                return AddDebugEntryResult.UploadedAndAssociated;
            }

            m_counters.IncrementCounter(SymbolClientCounter.NumFilesAssociated);
            return AddDebugEntryResult.Associated;
        }

        /// <summary>
        /// Finalizes a symbol request.
        /// </summary>        
        public async Task<Request> FinalizeAsync(CancellationToken token)
        {
            using (m_counters.StartStopwatch(SymbolClientCounter.FinalizeDuration))
            {
                var result = await m_symbolClient.FinalizeRequestAsync(
                    RequestId,
                    ComputeExpirationDate(m_config.Retention),
                    // isUpdateOperation == true => request will be marked as 'Sealed', 
                    // i.e., no more DebugEntries could be added to it 
                    isUpdateOperation: false,
                    token);

                return result;
            }
        }

        /// <inheritdoc />
        public Task<Request> FinalizeAsync() => FinalizeAsync(CancellationToken);

        private DateTime ComputeExpirationDate(TimeSpan retention)
        {
            return DateTime.UtcNow.Add(retention);
        }

        /// <nodoc />
        public void Dispose()
        {
            m_symbolClient.Dispose();
        }

        private static DebugEntry CreateDebugEntry(IDebugEntryData data, IDomainId domainId)
        {
            return new DebugEntry()
            {
                BlobIdentifier = data.BlobIdentifier,
                ClientKey = data.ClientKey,
                InformationLevel = data.InformationLevel,
                DomainId = domainId,
            };
        }

        /// <inheritdoc />
        public IDictionary<string, long> GetStats()
        {
            return m_counters.AsStatistics("SymbolDaemon");
        }

        private enum SymbolClientCounter
        {
            [CounterType(CounterType.Stopwatch)]
            AuthDuration,

            [CounterType(CounterType.Stopwatch)]
            GetRequestIdDuration,

            [CounterType(CounterType.Stopwatch)]
            CreateDuration,

            [CounterType(CounterType.Stopwatch)]
            FinalizeDuration,

            [CounterType(CounterType.Stopwatch)]
            TotalAssociateTime,

            [CounterType(CounterType.Stopwatch)]
            TotalAssociateAfterUploadTime,

            [CounterType(CounterType.Stopwatch)]
            TotalUploadTime,

            NumAddFileRequests,

            NumFilesWithoutDebugEntries,

            NumFilesAssociated,

            NumFilesUploaded,

            TotalUploadSize,
        }
    }
}