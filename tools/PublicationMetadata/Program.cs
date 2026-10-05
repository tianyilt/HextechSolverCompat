using System.Text.Json;
using Steamworks;

// Read actual public item metadata. No publishing, subscriptions or account output.
if (args.Length != 1 || !ulong.TryParse(args[0], out ulong id)) return 2;
if (!SteamAPI.Init()) { Console.Error.WriteLine("SteamAPI.Init failed"); return 3; }
UGCQueryHandle_t handle = UGCQueryHandle_t.Invalid;
try
{
    handle = SteamUGC.CreateQueryUGCDetailsRequest([new PublishedFileId_t(id)], 1);
    SteamUGC.SetReturnChildren(handle, true);
    SteamUGC.SetReturnAdditionalPreviews(handle, true);
    SteamUGC.SetReturnLongDescription(handle, true);
    bool done = false, failed = false;
    SteamUGCQueryCompleted_t completion = default;
    using var callback = CallResult<SteamUGCQueryCompleted_t>.Create((value, ioFailure) =>
    { completion = value; failed = ioFailure; done = true; });
    callback.Set(SteamUGC.SendQueryUGCRequest(handle));
    var deadline = DateTime.UtcNow.AddSeconds(45);
    while (!done && DateTime.UtcNow < deadline)
    { SteamAPI.RunCallbacks(); Thread.Sleep(50); }
    if (!done || failed || completion.m_eResult != EResult.k_EResultOK)
    { Console.Error.WriteLine($"Query failed: done={done} ioFailure={failed} result={completion.m_eResult}"); return 4; }
    if (!SteamUGC.GetQueryUGCResult(handle, 0, out SteamUGCDetails_t details)
        || details.m_eResult != EResult.k_EResultOK) return 5;
    var children = new PublishedFileId_t[details.m_unNumChildren];
    if (children.Length > 0 && !SteamUGC.GetQueryUGCChildren(handle, 0, children, (uint)children.Length)) return 6;
    if (!SteamUGC.GetQueryUGCPreviewURL(handle, 0, out string cover, 8192)) return 7;
    var previews = new List<object>();
    uint count = SteamUGC.GetQueryUGCNumAdditionalPreviews(handle, 0);
    for (uint index = 0; index < count; index++)
    {
        if (!SteamUGC.GetQueryUGCAdditionalPreview(handle, 0, index,
            out string url, 8192, out string originalName, 8192, out EItemPreviewType type)) return 8;
        previews.Add(new { url, originalName, type = type.ToString() });
    }
    Console.WriteLine(JsonSerializer.Serialize(new {
        id, result = details.m_eResult.ToString(), title = details.m_rgchTitle,
        description = details.m_rgchDescription, visibility = details.m_eVisibility.ToString(),
        consumerAppId = details.m_nConsumerAppID.m_AppId,
        dependencies = children.Select(child => child.m_PublishedFileId).Order().ToArray(),
        cover, previews, tags = details.m_rgchTags
    }));
    return 0;
}
finally
{
    if (handle != UGCQueryHandle_t.Invalid) SteamUGC.ReleaseQueryUGCRequest(handle);
    SteamAPI.Shutdown();
}
