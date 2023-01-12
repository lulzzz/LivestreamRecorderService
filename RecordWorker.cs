﻿using Azure.Storage.Files.Shares.Models;
using LivestreamRecorderService.DB.Enum;
using LivestreamRecorderService.DB.Models;
using LivestreamRecorderService.Interfaces;
using LivestreamRecorderService.Models.Options;
using LivestreamRecorderService.ScopedServices;
using LivestreamRecorderService.SingletonServices;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace LivestreamRecorderService;

public class RecordWorker : BackgroundService
{
    private readonly ILogger<RecordWorker> _logger;
    private readonly IACIService _aCIService;
    private readonly ACIYtarchiveService _aCIYtarchiveService;
    private readonly ACIYtdlpService _aCIYtdlpService;
    private readonly ACITwitcastingRecorderService _aCITwitcastingRecorderService;
    private readonly ACIStreamlinkService _aCIStreamlinkService;
    private readonly IAFSService _aFSService;
    private readonly IServiceProvider _serviceProvider;

    public RecordWorker(
        ILogger<RecordWorker> logger,
        IACIService aCIService,
        ACIYtarchiveService aCIYtarchiveService,
        ACIYtdlpService aCIYtdlpService,
        ACITwitcastingRecorderService aCITwitcastingRecorderService,
        ACIStreamlinkService aCIStreamlinkService,
        IAFSService aFSService,
        IOptions<AzureOption> options,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _aCIService = aCIService;
        _aCIYtarchiveService = aCIYtarchiveService;
        _aCIYtdlpService = aCIYtdlpService;
        _aCITwitcastingRecorderService = aCITwitcastingRecorderService;
        _aCIStreamlinkService = aCIStreamlinkService;
        _aFSService = aFSService;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var __ = LogContext.PushProperty("Worker", nameof(RecordWorker));

        _logger.LogInformation("{Worker} will sleep 30 seconds to wait for {WorkerToWait} to start.", nameof(RecordWorker), nameof(MonitorWorker));
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        _logger.LogTrace("{Worker} starts...", nameof(RecordWorker));
        while (!stoppingToken.IsCancellationRequested)
        {
            using var ____ = LogContext.PushProperty("WorkerRunId", $"{nameof(RecordWorker)}_{DateTime.Now:yyyyMMddHHmmssfff}");
            #region DI
            using var scope = _serviceProvider.CreateScope();
            var videoService = scope.ServiceProvider.GetRequiredService<VideoService>();
            #endregion

            await CreateACIStartRecordAsync(videoService, stoppingToken);
            await CreateACIStartDownloadAsync(videoService, stoppingToken);

            videoService.RollbackVideosStatusStuckAtUploading();

            var finished = await MonitorRecordingVideosAsync(videoService, stoppingToken);
            foreach (var kvp in finished)
            {
                var (video, files) = (kvp.Key, kvp.Value);
                using var ___ = LogContext.PushProperty("videoId", video.id);

                try
                {
                    await _aCIService.RemoveCompletedInstanceContainerAsync(video, stoppingToken);
                }
                catch (Exception)
                {
                    videoService.UpdateVideoStatus(video, VideoStatus.Error);
                }

                videoService.AddFilesToVideo(video, files);
                await videoService.TransferVideoToBlobStorageAsync(video, stoppingToken);
            }
            _logger.LogTrace("{Worker} ends. Sleep 5 minutes.", nameof(RecordWorker));
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    /// <summary>
    /// CreateACIStartRecord
    /// </summary>
    /// <param name="videoService"></param>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    private async Task CreateACIStartRecordAsync(VideoService videoService, CancellationToken stoppingToken)
    {
        _logger.LogDebug("Getting videos to record");
        var videos = videoService.GetVideosByStatus(VideoStatus.WaitingToRecord);

        if (videos.Count > 0)
            _logger.LogInformation("Get {count} videos to record: {videoIds}", videos.Count, string.Join(", ", videos.Select(p => p.id).ToList()));
        else
            _logger.LogDebug("Get {count} videos to record", videos.Count);

        foreach (var video in videos)
        {
            using var _ = LogContext.PushProperty("videoId", video.id);
            _logger.LogInformation("Start to create ACI: {videoId}", video.id);
            switch (video.Channel.Source)
            {
                case "Youtube":
                    await _aCIYtarchiveService.StartInstanceAsync(
                        $"https://youtu.be/{video.id}",
                        stoppingToken);
                    break;
                case "Twitcasting":
                    await _aCITwitcastingRecorderService.StartInstanceAsync(
                        video.ChannelId,
                        stoppingToken);
                    break;
                case "Twitch":
                    await _aCIStreamlinkService.StartInstanceAsync(
                        video.ChannelId,
                        stoppingToken);
                    break;
                default:
                    _logger.LogError("ACI deployment FAILED, Source not support: {source}", video.Channel.Source);
                    throw new NotSupportedException($"Source {video.Channel.Source} not supported");
            }
            videoService.UpdateVideoStatus(video, VideoStatus.Recording);

            _logger.LogInformation("ACI deployed: {videoId} ", video.id);
            _logger.LogInformation("Start to record {videoId}", video.id);
        }
    }

    private async Task CreateACIStartDownloadAsync(VideoService videoService, CancellationToken stoppingToken)
    {
        _logger.LogDebug("Getting videos to download");
        var videos = videoService.GetVideosByStatus(VideoStatus.WaitingToDownload);
        if (videos.Count > 0)
            _logger.LogInformation("Get {count} videos to download: {videoIds}", videos.Count, string.Join(", ", videos.Select(p => p.id).ToList()));
        else
            _logger.LogDebug("Get {count} videos to download", videos.Count);

        foreach (var video in videos)
        {
            using var _ = LogContext.PushProperty("videoId", video.id);
            _logger.LogInformation("Start to create ACI: {videoId}", video.id);
            switch (video.Channel.Source)
            {
                case "Youtube":
                    await _aCIYtdlpService.StartInstanceAsync(
                        $"https://youtu.be/{video.id}",
                        stoppingToken);
                    break;
                case "Twitcasting":
                    await _aCIYtdlpService.StartInstanceAsync(
                        $"https://twitcasting.tv/{video.ChannelId}/movie/{video.id}",
                        stoppingToken);
                    break;
                case "Twitch":
                    await _aCIYtdlpService.StartInstanceAsync(
                        $"https://www.twitch.tv/videos/{video.id}",
                        stoppingToken);
                    break;
                default:
                    _logger.LogError("ACI deployment FAILED, Source not support: {source}", video.Channel.Source);
                    throw new NotSupportedException($"Source {video.Channel.Source} not supported");
            }
            videoService.UpdateVideoStatus(video, VideoStatus.Downloading);
            _logger.LogInformation("ACI deployed: {videoId} ", video.id);
            _logger.LogInformation("Start to download {videoId}", video.id);
        }
    }

    /// <summary>
    /// Check recordings status and return finished videos
    /// </summary>
    /// <param name="videoService"></param>
    /// <returns>Videos that finish recording.</returns>
    private async Task<Dictionary<Video, List<ShareFileItem>>> MonitorRecordingVideosAsync(VideoService videoService, CancellationToken cancellation = default)
    {
        var finishedRecordingVideos = new Dictionary<Video, List<ShareFileItem>>();
        var videos = videoService.GetVideosByStatus(VideoStatus.Recording)
             .Concat(videoService.GetVideosByStatus(VideoStatus.Downloading));
        foreach (var video in videos)
        {
            using var _ = LogContext.PushProperty("videoId", video.id);
            var files = await _aFSService.GetShareFilesByVideoIdAsync(videoId: video.id,
                                                                      delay: TimeSpan.FromMinutes(5),
                                                                      cancellation: cancellation);

            if (files.Count > 0)
            {
                _logger.LogInformation("Video recording finish {videoId}", video.id);
                finishedRecordingVideos.Add(video, files);
            }
        }
        return finishedRecordingVideos;
    }
}