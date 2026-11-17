using System.Net;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using TimeShift.Application.Abstractions;

namespace TimeShift.Infrastructure.Push;

/// <summary>Web Push через Lib.Net.Http.WebPush: VAPID, шифрование aes128gcm, срочность высокая.</summary>
internal sealed class LibWebPushSender : IWebPushSender
{
    private readonly PushServiceClient _client = new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    });

    public async Task<PushOutcome> SendAsync(PushRequest request, VapidKeys keys, CancellationToken cancellationToken)
    {
        var subscription = new Lib.Net.Http.WebPush.PushSubscription
        {
            Endpoint = request.Endpoint,
            Keys = new Dictionary<string, string> { ["p256dh"] = request.P256dh, ["auth"] = request.Auth },
        };
        var message = new PushMessage(request.Payload)
        {
            Urgency = PushMessageUrgency.High,
            TimeToLive = (int)request.TimeToLive.TotalSeconds,
            Topic = request.Topic,
        };

        try
        {
            await _client.RequestPushMessageDeliveryAsync(
                subscription, message, new VapidAuthentication(keys.PublicKey, keys.PrivateKey) { Subject = keys.Subject }, cancellationToken);
            return PushOutcome.Delivered;
        }
        catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushOutcome.Gone;
        }
    }
}
