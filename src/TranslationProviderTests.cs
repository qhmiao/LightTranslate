using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LightTranslate
{
    internal static class TranslationProviderTests
    {
        internal static int RunStandalone()
        {
            var results = new Dictionary<string, object>(); Run(results);
            File.WriteAllText(Path.Combine(Program.Folder, "provider-test.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(results));
            return 0;
        }
        private sealed class Handler : HttpMessageHandler
        {
            internal int MicrosoftCalls, TencentCalls;
            internal bool FailMicrosoft;
            internal string LastTarget, LastTencentBody;
            internal TaskCompletionSource<HttpResponseMessage> Pending;
            internal static HttpResponseMessage Success(string json)
            { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (request.RequestUri.Host == "api.cognitive.microsofttranslator.com")
                {
                    MicrosoftCalls++; LastTarget = request.RequestUri.Query;
                    if (Pending != null) return Pending.Task;
                    if (FailMicrosoft) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    return Task.FromResult(Success("[{\"translations\":[{\"text\":\"Microsoft fixture\"}]}]"));
                }
                if (request.RequestUri.Host != "transmart.qq.com") throw new Exception("Unexpected translation destination");
                TencentCalls++; LastTencentBody = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return Task.FromResult(Success("{\"auto_translation\":\"Tencent fixture\"}"));
            }
        }
        private static void Check(bool ok, string message)
        { if (!ok) throw new Exception("Translation provider: " + message); }

        internal static void Run(Dictionary<string, object> results)
        {
            DeepLTests.Run(results);
            var handler = new Handler();
            using (var translator = new Translator(handler))
            {
                var none = CancellationToken.None;
                Check(translator.Translate("sample", "ja", none).Result == "Microsoft fixture", "default Microsoft");
                translator.Translate("sample", "ja", none).GetAwaiter().GetResult();
                Check(handler.MicrosoftCalls == 1 && handler.TencentCalls == 0, "same policy cache");
                translator.Translate("sample", "ja", none, "microsoft").GetAwaiter().GetResult();
                Check(handler.MicrosoftCalls == 2, "explicit provider cache isolation");
                translator.Translate("sample", "fr", none, "microsoft").GetAwaiter().GetResult();
                Check(handler.MicrosoftCalls == 3 && handler.LastTarget.Contains("to=fr"), "target cache isolation");
                Check(translator.Translate("sample", "fr", none, "tencent").Result == "Tencent fixture", "Tencent choice");
                Check(handler.MicrosoftCalls == 3 && handler.TencentCalls == 1 && handler.LastTencentBody.Contains("\"lang\":\"zh\""), "Tencent fixed Chinese");
                handler.FailMicrosoft = true;
                Check(translator.Translate("fallback", "ja", none).Result == "Tencent fixture" && handler.TencentCalls == 2, "automatic fallback retained");
                bool failed = false;
                try { translator.Translate("failure", "ja", none, "microsoft").GetAwaiter().GetResult(); }
                catch (HttpRequestException) { failed = true; }
                Check(failed && handler.TencentCalls == 2, "explicit Microsoft never falls back");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool stopped = false;
                    try { translator.Translate("sample", "ja", canceled.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { stopped = true; }
                    Check(stopped, "cancellation precedes cache hit");
                }
                handler.FailMicrosoft = false;
                handler.Pending = new TaskCompletionSource<HttpResponseMessage>();
                using (var canceled = new CancellationTokenSource())
                {
                    var pending = translator.Translate("pending", "en", canceled.Token, "microsoft");
                    canceled.Cancel();
                    handler.Pending.SetResult(Handler.Success("[{\"translations\":[{\"text\":\"old response\"}]}]"));
                    bool stopped = false;
                    try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                    string cached;
                    Check(stopped && !translator.TryCached("pending", "en", out cached, "microsoft"), "canceled old response cannot enter cache");
                }
            }

            string root = Path.Combine(Program.Folder, "self-test-provider-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "settings.json");
                File.WriteAllText(path, "{\"Target\":\"ja\"}");
                var settings = Settings.Load(path);
                Check(settings.TranslationProvider == "auto", "legacy default");
                settings.TranslationProvider = "unsupported"; settings.NormalizeReading();
                Check(settings.TranslationProvider == "auto", "unknown provider normalization");
                int applied = 0;
                var window = new SettingsWindow(settings, delegate { applied++; }, new FakeStartupRegistration());
                window.SetProviderForTest(2);
                Check(!window.TargetEnabledForTest && settings.TranslationProvider == "auto", "unsaved Tencent choice is local and disables target");
                Check(window.Apply(path), "save provider");
                Check(settings.TranslationProvider == "tencent" && settings.Target == "ja" && applied == 1, "live apply preserves Microsoft target");
                var reopened = new SettingsWindow(Settings.Load(path), null, new FakeStartupRegistration());
                Check(reopened.ProviderForTest == 2 && !reopened.TargetEnabledForTest, "reopen persisted provider");
                reopened.SetProviderForTest(1);
                Check(reopened.TargetEnabledForTest, "Microsoft target reenabled");
                reopened.Close();
                Check(Settings.Load(path).TranslationProvider == "tencent", "cancel preserves saved provider");
                var switching = new SettingsWindow(settings, delegate { applied++; }, new FakeStartupRegistration());
                switching.SetProviderForTest(1); Check(switching.Apply(path), "runtime provider switch");
                Check(settings.TranslationProvider == "microsoft" && applied == 2, "switch callback invoked");
            }
            finally { Directory.Delete(root, true); }
            results["translationProviders"] = "legacy/default, save/cancel/reopen/live switch, provider/target cache isolation, explicit routing, fallback and cancellation passed with fake HTTP; no network requests";
        }
    }
}
