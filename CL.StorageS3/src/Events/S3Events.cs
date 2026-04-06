using CodeLogic.Core.Events;

namespace CL.StorageS3.Events;

/// <summary>
/// Published when an object has been successfully uploaded to S3.
/// </summary>
public record ObjectUploadedEvent(
    string ConnectionId,
    string BucketName,
    string Key,
    long Size,
    DateTime UploadedAt) : IEvent;

/// <summary>
/// Published when an object has been successfully deleted from S3.
/// </summary>
public record ObjectDeletedEvent(
    string ConnectionId,
    string BucketName,
    string Key,
    DateTime DeletedAt) : IEvent;

/// <summary>
/// Published when a new bucket has been successfully created.
/// </summary>
public record BucketCreatedEvent(
    string ConnectionId,
    string BucketName,
    DateTime CreatedAt) : IEvent;
