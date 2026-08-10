using Amazon.S3;
using Amazon.S3.Model;

namespace RaceNetScraper.App.Services;

/// <summary>Uploads exported JSON straight into the configured bucket/folder with no further
/// nesting — unlike the local export, which always keeps one subfolder per meeting.</summary>
public static class S3JsonUploader
{
    public static async Task UploadAsync(
        AppSettings settings, string fileName, string jsonContent, CancellationToken cancellationToken = default)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = settings.S3Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };

        using var client = new AmazonS3Client(settings.S3AccessKey, settings.S3SecretKey, config);

        var folder = settings.S3Folder.Trim('/');
        var key = folder.Length > 0 ? $"{folder}/{fileName}" : fileName;

        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.S3BucketName,
            Key = key,
            ContentBody = jsonContent,
            ContentType = "application/json",
        }, cancellationToken);
    }
}
