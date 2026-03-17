using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Cmux.Core.Config;
using Cmux.Core.Terminal;

namespace Cmux.Core.Services;

public partial class PortDetectionService
{
    [GeneratedRegex(@"https?://(?:localhost|127\.0\.0\.1):\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlPattern();

    private readonly ConcurrentDictionary<string, HashSet<string>> _detectedUrls = new();

    public event Action<string, string>? DevServerStarted;

    public void RegisterPane(string paneId, TerminalSession session)
    {
        _detectedUrls[paneId] = [];

        session.RawOutputReceived += data =>
        {
            if (!SettingsService.Current.AutoOpenBrowserOnDevServer) return;

            var text = Encoding.UTF8.GetString(data);
            var matches = UrlPattern().Matches(text);
            foreach (Match match in matches)
            {
                var url = match.Value;
                var urls = _detectedUrls.GetOrAdd(paneId, _ => []);
                if (urls.Add(url))
                    DevServerStarted?.Invoke(paneId, url);
            }
        };
    }

    public void UnregisterPane(string paneId)
    {
        _detectedUrls.TryRemove(paneId, out _);
    }
}
