﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.Webhook.Helpers;
using Jellyfin.Plugin.Webhook.Models;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using Microsoft.Extensions.Logging;
using Constants = Jellyfin.Plugin.Webhook.Configuration.Constants;

namespace Jellyfin.Plugin.Webhook.Notifiers
{
    /// <summary>
    /// Notifier when a library item is added.
    /// </summary>
    public class LibraryAddedNotifier : INotificationService, IDisposable
    {
        private readonly ILogger<LibraryAddedNotifier> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationHost _applicationHost;
        private readonly WebhookSender _webhookSender;

        private readonly ConcurrentDictionary<Guid, QueuedItemContainer> _itemProcessQueue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _periodicAsyncTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryAddedNotifier"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{LibraryAddedNotifier}"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="applicationHost">Instance of the <see cref="IApplicationHost"/> interface.</param>
        /// <param name="webhookSender">Instance of the <see cref="WebhookSender"/>.</param>
        public LibraryAddedNotifier(
            ILogger<LibraryAddedNotifier> logger,
            ILibraryManager libraryManager,
            IApplicationHost applicationHost,
            WebhookSender webhookSender)
        {
            _logger = logger;
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _applicationHost = applicationHost;
            _webhookSender = webhookSender;

            _itemProcessQueue = new ConcurrentDictionary<Guid, QueuedItemContainer>();
            _libraryManager.ItemAdded += ItemAddedHandler;

            HandlebarsFunctionHelpers.RegisterHelpers();
            _cancellationTokenSource = new CancellationTokenSource();
            _periodicAsyncTask = PeriodicAsyncHelper.PeriodicAsync(
                async () =>
                {
                    try
                    {
                        await ProcessItemsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error");
                    }
                }, TimeSpan.FromMilliseconds(Constants.RecheckIntervalMs),
                _cancellationTokenSource.Token);
        }

        /// <inheritdoc />
        public string Name => WebhookPlugin.Instance?.Name ?? throw new NullReferenceException(nameof(WebhookPlugin.Instance.Name));

        /// <inheritdoc />
        public Task SendNotification(UserNotification request, CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public bool IsEnabledForUser(User user) => true;

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose.
        /// </summary>
        /// <param name="disposing">Dispose all assets.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _libraryManager.ItemAdded -= ItemAddedHandler;
                _cancellationTokenSource.Cancel();
                _periodicAsyncTask.GetAwaiter().GetResult();
                _cancellationTokenSource.Dispose();
            }
        }

        private void ItemAddedHandler(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            // Never notify on virtual items.
            if (itemChangeEventArgs.Item.IsVirtualItem)
            {
                return;
            }

            _itemProcessQueue.TryAdd(itemChangeEventArgs.Item.Id, new QueuedItemContainer(itemChangeEventArgs.Item.Id));
            _logger.LogDebug("Queued {itemName} for notification.", itemChangeEventArgs.Item.Name);
        }

        private async Task ProcessItemsAsync()
        {
            _logger.LogDebug("ProcessItemsAsync");
            // Attempt to process all items in queue.
            var currentItems = _itemProcessQueue.ToArray();
            foreach (var (key, container) in currentItems)
            {
                var item = _libraryManager.GetItemById(key);
                _logger.LogDebug("Item {itemName}", item.Name);

                // Metadata not refreshed yet and under retry limit.
                if (item.ProviderIds.Keys.Count == 0 && container.RetryCount < Constants.MaxRetries)
                {
                    _logger.LogDebug("Requeue {itemName}, no provider ids.", item.Name);
                    container.RetryCount++;
                    _itemProcessQueue.AddOrUpdate(key, container, (_, _) => container);
                    continue;
                }

                _logger.LogDebug("Notifying for {itemName}", item.Name);

                // Send notification to each configured destination.
                var itemData = GetDataObject(item);
                var itemType = item.GetType();
                await _webhookSender.SendItemAddedNotification(itemData, itemType);

                // Remove item from queue.
                _itemProcessQueue.TryRemove(key, out _);
            }
        }

        private Dictionary<string, object> GetDataObject(BaseItem item)
        {
            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            data["Timestamp"] = DateTime.Now;
            data["UtcTimestamp"] = DateTime.UtcNow;
            data["Name"] = item.Name;
            data["Overview"] = item.Overview;
            data["ItemId"] = item.Id;
            data["ServerId"] = _applicationHost.SystemId;
            data["ServerUrl"] = WebhookPlugin.Instance?.Configuration.ServerUrl ?? "localhost:8096";
            data["ServerName"] = _applicationHost.Name;
            data["ItemType"] = item.GetType().Name;

            if (item.ProductionYear.HasValue)
            {
                data["Year"] = item.ProductionYear;
            }

            switch (item)
            {
                case Season:
                    if (!string.IsNullOrEmpty(item.Parent?.Name))
                    {
                        data["SeriesName"] = item.Parent.Name;
                    }

                    if (item.Parent?.ProductionYear != null)
                    {
                        data["Year"] = item.Parent.ProductionYear;
                    }

                    if (item.IndexNumber.HasValue)
                    {
                        data["SeasonNumber"] = item.IndexNumber;
                        data["SeasonNumber00"] = item.IndexNumber.Value.ToString("00", CultureInfo.InvariantCulture);
                        data["SeasonNumber000"] = item.IndexNumber.Value.ToString("000", CultureInfo.InvariantCulture);
                    }

                    break;
                case Episode:
                    if (!string.IsNullOrEmpty(item.Parent?.Parent?.Name))
                    {
                        data["SeriesName"] = item.Parent.Parent.Name;
                    }

                    if (item.Parent?.IndexNumber != null)
                    {
                        data["SeasonNumber"] = item.Parent.IndexNumber;
                        data["SeasonNumber00"] = item.Parent.IndexNumber.Value.ToString("00", CultureInfo.InvariantCulture);
                        data["SeasonNumber000"] = item.Parent.IndexNumber.Value.ToString("000", CultureInfo.InvariantCulture);
                    }

                    if (item.IndexNumber.HasValue)
                    {
                        data["EpisodeNumber"] = item.IndexNumber;
                        data["EpisodeNumber00"] = item.IndexNumber.Value.ToString("00", CultureInfo.InvariantCulture);
                        data["EpisodeNumber000"] = item.IndexNumber.Value.ToString("000", CultureInfo.InvariantCulture);
                    }

                    if (item.Parent?.Parent?.ProductionYear != null)
                    {
                        data["Year"] = item.Parent.Parent.ProductionYear;
                    }

                    break;
            }

            foreach (var (providerKey, providerValue) in item.ProviderIds)
            {
                data[$"Provider_{providerKey.ToLowerInvariant()}"] = providerValue;
            }

            return data;
        }
    }
}