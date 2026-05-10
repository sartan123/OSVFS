using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.ObjectStore;
using OSVFS.Sync;
using OSVFS.Sync.Sqs;
using Xunit;

namespace OSVFS.Core.UnitTests.Sync;

/// <summary>
/// Verifies the provider-aware dispatch in <see cref="ChangeNotificationFactory"/>.
/// The S3 arm goes through <see cref="SqsChangeSourceFactory"/>; GCS / Azure
/// arms throw <see cref="NotSupportedException"/> with a "use polling" hint
/// until the matching backends land in Phase 2.
/// </summary>
public class ChangeNotificationFactoryTests
{
    [Fact]
    public void Create_returns_SqsChangeSource_for_S3_provider()
    {
        var source = ChangeNotificationFactory.Create(
            ObjectStoreProvider.S3,
            queueOrSubscription: "https://sqs.us-east-1.amazonaws.com/123456789012/osvfs-changes",
            bucketName: "my-bucket",
            keyPrefix: null,
            endpointUrl: null,
            region: "us-east-1",
            credentials: null,
            NullLoggerFactory.Instance);

        Assert.IsType<SqsChangeSource>(source);
        // Dispose the source so the SQS client it owns is released. Async dispose
        // returns a ValueTask; bridge to sync here since the test does not need
        // async semantics.
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void Create_throws_NotSupportedException_for_Gcs()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ChangeNotificationFactory.Create(
                ObjectStoreProvider.Gcs,
                queueOrSubscription: "projects/p/subscriptions/s",
                bucketName: "my-bucket",
                keyPrefix: null,
                endpointUrl: null,
                region: null,
                credentials: null,
                NullLoggerFactory.Instance));

        // The hint must point operators at the polling fallback; otherwise the
        // failure is just a dead end.
        Assert.Contains("polling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_throws_NotSupportedException_for_AzureBlob()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ChangeNotificationFactory.Create(
                ObjectStoreProvider.AzureBlob,
                queueOrSubscription: "https://account.queue.core.windows.net/queue",
                bucketName: "my-container",
                keyPrefix: null,
                endpointUrl: null,
                region: null,
                credentials: null,
                NullLoggerFactory.Instance));

        Assert.Contains("polling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
