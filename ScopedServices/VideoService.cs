﻿using Azure.Storage.Files.Shares.Models;
using LivestreamRecorder.DB.Core;
using LivestreamRecorder.DB.Enum;
using LivestreamRecorder.DB.Interfaces;
using LivestreamRecorder.DB.Models;
using LivestreamRecorderService.Models;
using LivestreamRecorderService.SingletonServices;
using Newtonsoft.Json;
using System.Net;
using System.Web;

namespace LivestreamRecorderService.ScopedServices;

public class VideoService
{
    private readonly ILogger<VideoService> _logger;
    private readonly IUnitOfWork _unitOfWork_Public;
    private readonly IVideoRepository _videoRepository;
    private readonly DiscordService _discordService;
    private readonly IHttpClientFactory _httpFactory;

    public VideoService(
        ILogger<VideoService> logger,
        UnitOfWork_Public unitOfWork_Public,
        IVideoRepository videoRepository,
        DiscordService discordService,
        IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _unitOfWork_Public = unitOfWork_Public;
        _videoRepository = videoRepository;
        _discordService = discordService;
        _httpFactory = httpFactory;
    }

    public List<Video> GetVideosByStatus(VideoStatus status)
        => _videoRepository.Where(p => p.Status == status)
                           .Select(p => _videoRepository.LoadRelatedData(p))
                           .ToList();

    public void UpdateVideoStatus(Video video, VideoStatus status)
    {
        _unitOfWork_Public.ReloadEntityFromDB(video);
        video.Status = status;
        _videoRepository.Update(video);
        _unitOfWork_Public.Commit();
        _logger.LogDebug("Update Video {videoId} Status to {videostatus}", video.id, status);
    }

    public void UpdateVideoNote(Video video, string? Note)
    {
        _unitOfWork_Public.ReloadEntityFromDB(video);
        video.Note = Note;
        _videoRepository.Update(video);
        _unitOfWork_Public.Commit();
        _logger.LogDebug("Update Video {videoId} note", video.id);
    }

    public void AddFilePropertiesToVideo(Video video, List<ShareFileItem> sharefileItems)
    {
        video = _videoRepository.GetById(video.id);
        _videoRepository.LoadRelatedData(video);

        var videoFile = sharefileItems.FirstOrDefault(p => p.Name.Split('.').Last() is "mp4" or "mkv" or "webm");
        if (null != videoFile)
        {
            video.Size = videoFile.FileSize;
            video.Filename = videoFile.Name;
        }
        else
        {
            _logger.LogWarning("No video file found for video {videoId}", video.id);
        }

        var thumbnail = sharefileItems.FirstOrDefault(p => p.Name.Split('.').Last() is "webp" or "jpg" or "jpeg" or "png");
        if (null != thumbnail)
        {
            video.Thumbnail = thumbnail.Name;
        }

        video.ArchivedTime = DateTime.UtcNow;
        _videoRepository.Update(video);
        _unitOfWork_Public.Commit();
    }

    public async Task TransferVideoToBlobStorageAsync(Video video, CancellationToken cancellation = default)
    {
        try
        {
            UpdateVideoStatus(video, VideoStatus.Uploading);

            _logger.LogInformation("Call Azure Function to transfer video to blob storage: {videoId}", video.id);
            using var client = _httpFactory.CreateClient("AzureFileShares2BlobContainers");

            // It is possible for Youtube to use "-" at the beginning of an id, which can cause errors when using the id as a file name.
            // Therefore, we add "_" before the file name to avoid such issues.
            var videoId = video.Source == "Youtube"
                          ? "_" + video.id
                          : video.id;

            // https://learn.microsoft.com/zh-tw/azure/azure-functions/durable/durable-functions-overview?tabs=csharp-inproc#async-http
            // https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-http-api#start-orchestration
            var startResponse = await client.PostAsync("api/AzureFileShares2BlobContainers?videoId=" + HttpUtility.UrlEncode(videoId), null, cancellation);
            startResponse.EnsureSuccessStatusCode();
            var responseContent = await startResponse.Content.ReadAsStringAsync(cancellation);
            var deserializedResponse = JsonConvert.DeserializeObject<AcceptedResponse>(responseContent);

            if (null == deserializedResponse) throw new Exception("Failed to serialize response from Durable Function start.");

            var statusUri = deserializedResponse.StatusQueryGetUri + "&returnInternalServerErrorOnFailure=true";
            var statusResponse = await client.GetAsync(statusUri, cancellation);

            while (statusResponse.StatusCode == HttpStatusCode.Accepted)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
                statusResponse = await client.GetAsync(statusUri, cancellation);
            }
            statusResponse.EnsureSuccessStatusCode();

            _logger.LogInformation("Video {videoId} is uploaded to Azure Storage.", video.id);
            UpdateVideoStatus(video, VideoStatus.Archived);
            await _discordService.SendArchivedMessage(video);
        }
        catch (Exception e)
        {
            UpdateVideoStatus(video, VideoStatus.Error);
            UpdateVideoNote(video, $"Exception happened when calling Azure Function to transfer files to blob storage. Please contact admin if you see this message.");
            _logger.LogError("Exception happened when calling Azure Function to transfer files to blob storage: {videoId}, {error}, {message}", video.id, e, e.Message);
        }
    }

    public void RollbackVideosStatusStuckAtUploading()
        => GetVideosByStatus(VideoStatus.Uploading)
            .Where(p => p.ArchivedTime.HasValue
                        && p.ArchivedTime.Value.AddMinutes(15) < DateTime.UtcNow)
            .ToList()
            .ForEach(p => UpdateVideoStatus(p, VideoStatus.Recording));
}
