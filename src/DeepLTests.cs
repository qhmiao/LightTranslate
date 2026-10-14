using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightTranslate
{
    internal static class DeepLTests
    {
        // Deliberately invalid sentinel. Never use an account key in tests or snapshots.
        private const string Sentinel = "test-only-not-a-real-deepl-key";
        private sealed class Credentials : IDeepLCredentials
        {
            internal string Key;
            internal bool FailWrite;
            internal int Reads;
            public bool Exists { get { return Key != null; } }
            public string Read() { Reads++; return Key; }
            public Action Replace(string key)
            {
                if (FailWrite) throw new IOException("Fixture");
                string previous = Key; Key = key; return delegate { Key = previous; };
            }
        }
        private sealed class Handler : HttpMessageHandler
        {
            internal int Calls;
            internal int Status = 200;
            internal bool Timeout, NetworkError, FailMicrosoft;
            internal string Host, Body, Auth, Path, Method;
            internal string Response = "{\"translations\":[{\"text\":\"DeepL fixture\"}]}";
            internal TaskCompletionSource<HttpResponseMessage> Pending;
            internal HttpResponseMessage Reply()
            { return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(Response) }; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++; Host = request.RequestUri.Host; Path = request.RequestUri.AbsolutePath; Method = request.Method.Method;
                Body = request.Content == null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Auth = request.Headers.Authorization == null ? "" : request.Headers.Authorization.ToString();
                if (Host == "api.cognitive.microsofttranslator.com" && FailMicrosoft)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (Host == "transmart.qq.com")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"auto_translation\":\"Tencent fixture\"}") });
                if (Timeout) throw new TaskCanceledException();
                if (NetworkError) throw new HttpRequestException("Do not expose " + Sentinel);
                return Pending == null ? Task.FromResult(Reply()) : Pending.Task;
            }
        }
        private static void Check(bool ok, string message)
        { if (!ok) throw new Exception("DeepL fixture: " + message); }
        private static void Error(Action action, string expected)
        {
            try { action(); }
            catch (DeepLException ex)
            {
                Check(ex.Message.Contains(expected) && !ex.ToString().Contains(Sentinel), "safe error category"); return;
            }
            throw new Exception("DeepL fixture: expected failure");
        }
        internal static void Run(Dictionary<string, object> results)
        {
            var credentials = new Credentials(); var deepL = new Handler(); var existing = new Handler();
            existing.Response = "[{\"translations\":[{\"text\":\"Microsoft fixture\"}]}]";
            var none = CancellationToken.None;
            using (var translator = new Translator(existing, credentials, deepL))
            {
                translator.Translate("sample", "zh-Hans", none).GetAwaiter().GetResult();
                Check(deepL.Calls == 0 && credentials.Reads == 0, "default never accesses DeepL");
                existing.FailMicrosoft = true;
                Check(translator.Translate("fallback", "ja", none).Result == "Tencent fixture", "default Microsoft/Tencent fallback unchanged");
                Check(deepL.Calls == 0 && credentials.Reads == 0, "automatic fallback never accesses DeepL");
                existing.FailMicrosoft = false;
                Error(delegate { translator.Translate("sample", "zh-Hans", none, "deepl").GetAwaiter().GetResult(); }, "先");
                Check(deepL.Calls == 0 && existing.Calls == 3, "unconfigured sends nothing and never falls back");
                credentials.Key = "invalid\r\nfixture";
                Error(delegate { translator.Translate("sample", "ja", none, "deepl").GetAwaiter().GetResult(); }, "先");
                Check(deepL.Calls == 0, "invalid credential never sent");
                credentials.Key = Sentinel;
                Check(translator.Translate("sample", "zh-Hans", none, "deepl").Result == "DeepL fixture", "explicit route and provider cache isolation");
                Check(deepL.Host == "api.deepl.com" && deepL.Path == "/v2/translate" && deepL.Method == "POST", "official endpoint");
                Check(deepL.Auth == "DeepL-Auth-Key " + Sentinel && deepL.Body.Contains("ZH-HANS") && !deepL.Body.Contains(Sentinel), "auth header and simplified Chinese");
                translator.Translate("sample", "zh-Hans", none, "deepl").GetAwaiter().GetResult();
                Check(deepL.Calls == 1, "DeepL cache");
                translator.Translate("sample", "zh-Hant", none, "deepl").GetAwaiter().GetResult();
                Check(deepL.Calls == 2 && deepL.Body.Contains("ZH-HANT"), "traditional Chinese target isolation");
                credentials.Key = Sentinel + ":fx";
                translator.Translate("sample", "zh-Hant", none, "deepl").GetAwaiter().GetResult();
                Check(deepL.Calls == 3 && deepL.Host == "api-free.deepl.com", "legacy suffix and credential cache isolation");
                credentials.Key = null;
                Error(delegate { translator.Translate("sample", "zh-Hant", none, "deepl").GetAwaiter().GetResult(); }, "先");
                Check(deepL.Calls == 3, "cleared credential disables cached and network result");
                credentials.Key = Sentinel;
                int[] statuses = { 401, 403, 456, 429, 400, 413, 500, 307 };
                string[] messages = { "认证", "认证", "上限", "频繁", "目标语言", "上限", "不可用", "不可用" };
                for (int i = 0; i < statuses.Length; i++)
                {
                    deepL.Status = statuses[i]; deepL.Response = Sentinel;
                    Error(delegate { translator.Translate("error", "ja", none, "deepl").GetAwaiter().GetResult(); }, messages[i]);
                }
                deepL.Status = 200; deepL.Response = "malformed " + Sentinel;
                Error(delegate { translator.Translate("error", "ja", none, "deepl").GetAwaiter().GetResult(); }, "解析");
                deepL.Timeout = true;
                Error(delegate { translator.Translate("error", "ja", none, "deepl").GetAwaiter().GetResult(); }, "超时");
                deepL.Timeout = false; deepL.NetworkError = true;
                Error(delegate { translator.Translate("error", "ja", none, "deepl").GetAwaiter().GetResult(); }, "网络");
                deepL.NetworkError = false;
                int before = deepL.Calls;
                Error(delegate { translator.Translate("error", "invalid target", none, "deepl").GetAwaiter().GetResult(); }, "代码");
                Error(delegate { translator.Translate(new string('a', 132000), "ja", none, "deepl").GetAwaiter().GetResult(); }, "上限");
                Check(deepL.Calls == before && existing.Calls == 3, "invalid input never sent, DeepL errors never fall back");
                deepL.Response = "{\"translations\":[{\"text\":\"DeepL fixture\"}]}";
                for (int mode = 0; mode < 2; mode++)
                {
                    deepL.Pending = new TaskCompletionSource<HttpResponseMessage>();
                    using (var cancellation = new CancellationTokenSource())
                    {
                        var task = translator.Translate("pending", "ja", cancellation.Token, "deepl");
                        if (mode == 0) cancellation.Cancel(); else credentials.Key = Sentinel + ":fx";
                        deepL.Pending.SetResult(deepL.Reply());
                        bool stopped = false;
                        try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                        Check(stopped, "canceled/credential-replaced response discarded");
                    }
                    deepL.Pending = null; credentials.Key = Sentinel;
                }
                before = deepL.Calls;
                translator.Translate("pending", "ja", none, "deepl").GetAwaiter().GetResult();
                Check(deepL.Calls == before + 1, "discarded result not cached");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel(); bool stopped = false;
                    try { translator.Translate("pending", "ja", cancellation.Token, "deepl").GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { stopped = true; }
                    Check(stopped && deepL.Calls == before + 1, "cancellation precedes cache");
                }
            }
            TestStorageAndSettings();
            results["deepL"] = "mock HTTP only: official endpoints/auth, Chinese targets, no-key/no-fallback, errors, quota, rate limit, timeout, cancellation, isolated caches; DPAPI and settings save/clear/cancel/rollback passed";
        }
        private static void TestStorageAndSettings()
        {
            string root = Path.Combine(Program.Folder, "self-test-deepl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string keyPath = Path.Combine(root, "deepl-key.bin"), settingsPath = Path.Combine(root, "settings.json");
                var store = new DeepLCredentials(keyPath);
                var undo = store.Replace(Sentinel);
                Check(store.Read() == Sentinel && !Encoding.UTF8.GetString(File.ReadAllBytes(keyPath)).Contains(Sentinel), "current-user encrypted roundtrip");
                undo(); Check(!store.Exists, "encrypted store rollback");
                store.Replace(Sentinel); var restore = store.Replace(null); Check(!store.Exists, "delete encrypted key");
                restore(); Check(store.Read() == Sentinel, "clear rollback");
                File.WriteAllText(keyPath, "corrupted fixture");
                Error(delegate { store.Read(); }, "重新保存");
                store.Replace(null);
                var settings = new Settings(); int applied = 0;
                var window = new SettingsWindow(settings, delegate { applied++; }, new FakeStartupRegistration(), store);
                window.SetProviderForTest(3); window.SetDeepLKeyForTest(Sentinel);
                Check(window.TargetEnabledForTest && !store.Exists, "credential edits are staged");
                Check(window.Apply(settingsPath) && store.Read() == Sentinel && applied == 1, "save credential and runtime provider callback");
                Check(settings.TranslationProvider == "deepl" && !File.ReadAllText(settingsPath).Contains(Sentinel), "config contains provider only");
                var reopened = new SettingsWindow(Settings.Load(settingsPath), null, new FakeStartupRegistration(), store);
                Check(reopened.ProviderForTest == 3 && reopened.DeepLInputEmptyForTest, "reopen without key refill");
                reopened.ClearDeepLKeyForTest(); reopened.Close();
                Check(store.Read() == Sentinel, "cancel pending clear");
                var canceled = new SettingsWindow(settings, null, new FakeStartupRegistration(), store);
                canceled.SetDeepLKeyForTest(Sentinel + ":fx"); canceled.Close();
                Check(store.Read() == Sentinel, "cancel pending replacement");
                var failure = new SettingsWindow(settings, null, new FakeStartupRegistration(), store);
                failure.SetDeepLKeyForTest(Sentinel + ":fx");
                Check(!failure.Apply(root) && store.Read() == Sentinel, "config failure restores previous encrypted key"); failure.Close();
                var failedClear = new SettingsWindow(settings, null, new FakeStartupRegistration(), store);
                failedClear.ClearDeepLKeyForTest();
                Check(!failedClear.Apply(root) && store.Read() == Sentinel, "config failure restores cleared key"); failedClear.Close();
                var badStore = new Credentials { FailWrite = true };
                var failedKey = new SettingsWindow(settings, null, new FakeStartupRegistration(), badStore);
                failedKey.SetProviderForTest(1); failedKey.SetDeepLKeyForTest(Sentinel);
                Check(!failedKey.Apply(settingsPath) && Settings.Load(settingsPath).TranslationProvider == "deepl", "credential failure preserves config"); failedKey.Close();
                var clear = new SettingsWindow(settings, null, new FakeStartupRegistration(), store);
                clear.ClearDeepLKeyForTest(); Check(clear.Apply(settingsPath) && !store.Exists, "saved clear");
                var preview = new SettingsWindow(settings, null, new FakeStartupRegistration(), store);
                preview.RenderPreview(Path.Combine(Program.Folder, "settings-deepl-preview.png"), 3);
                preview.RenderPreview(Path.Combine(Program.Folder, "settings-deepl-provider-preview.png"), 0); preview.Close();
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
