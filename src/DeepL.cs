using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LightTranslate
{
    // Messages are fixed locally: never include response bodies, keys or inner exceptions.
    internal sealed class DeepLException : Exception
    {
        internal DeepLException(string message) : base(message) { }
    }

    internal interface IDeepLCredentials
    {
        bool Exists { get; }
        string Read();
        Action Replace(string key);
    }

    internal sealed class DeepLCredentials : IDeepLCredentials
    {
        private readonly string path;
        internal DeepLCredentials(string path) { this.path = path; }
        internal static DeepLCredentials Current()
        { return new DeepLCredentials(Path.Combine(Program.Folder, "deepl-key.bin")); }
        public bool Exists { get { return File.Exists(path); } }
        public string Read()
        {
            if (!Exists) return null;
            byte[] plain = null;
            try
            {
                plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { throw new DeepLException("DeepL 密钥无法读取，请在设置中重新保存或清除。"); }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }
        internal static bool ValidKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 1024) return false;
            foreach (char c in key) if (c <= 32 || c >= 127) return false;
            return true;
        }
        public Action Replace(string key)
        {
            if (key != null && !ValidKey(key)) throw new DeepLException("请输入有效的 DeepL API 密钥，不要包含空白或换行。");
            byte[] previous = Exists ? File.ReadAllBytes(path) : null;
            byte[] plain = key == null ? null : Encoding.UTF8.GetBytes(key);
            try { Write(plain == null ? null : ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
            return delegate { Write(previous); };
        }
        private void Write(byte[] encrypted)
        {
            if (encrypted == null) { File.Delete(path); return; }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class DeepLTranslator : IDisposable
    {
        private readonly IDeepLCredentials credentials;
        private readonly HttpClient http;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        private string credentialScope;
        internal DeepLTranslator(IDeepLCredentials credentials, HttpMessageHandler handler = null)
        {
            this.credentials = credentials;
            // Never forward a selected text or key through an HTTP redirect.
            http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
            http.Timeout = TimeSpan.FromSeconds(7);
        }
        internal static string Target(string target)
        {
            string value = (target ?? "").Trim().ToUpperInvariant();
            if (value == "ZH" || value == "ZH-CN") value = "ZH-HANS";
            if (value == "ZH-TW") value = "ZH-HANT";
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Z]{2,3}(?:-[A-Z0-9]{1,8})*$"))
                throw new DeepLException("DeepL 目标语言代码无效，请检查设置。");
            return value;
        }
        internal static string Endpoint(string key)
        {
            // Same key-suffix selection as the official SDK; independent of plan names.
            return key.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate";
        }
        private static string Scope(string key)
        {
            if (key == null) return null;
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            try { using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes)); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        internal async Task<string> Translate(string text, string target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string key = credentials.Read();
            string scope = Scope(key);
            if (credentialScope != scope) { cache.Clear(); credentialScope = scope; }
            if (!DeepLCredentials.ValidKey(key)) throw new DeepLException("请先在设置的 DeepL 页保存 API 密钥。");
            string language = Target(target), cacheKey = language + "\n" + text, result;
            if (cache.TryGetValue(cacheKey, out result)) return result;
            string body = json.Serialize(new { text = new[] { text }, target_lang = language });
            if (Encoding.UTF8.GetByteCount(body) > 128 * 1024) throw new DeepLException("选中文字超过 DeepL 单次请求上限，请缩短选区。");
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(key)))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        int status = (int)response.StatusCode;
                        if (status == 401 || status == 403) throw new DeepLException("DeepL 认证失败，请检查 API 密钥及翻译权限。");
                        if (status == 456) throw new DeepLException("DeepL 用量或费用已达上限，请检查账户额度。");
                        if (status == 429) throw new DeepLException("DeepL 请求过于频繁，请稍后重试。");
                        if (status == 400) throw new DeepLException("DeepL 不接受此请求，请检查目标语言是否受支持。");
                        if (status == 413) throw new DeepLException("选中文字超过 DeepL 单次请求上限，请缩短选区。");
                        if (!response.IsSuccessStatusCode) throw new DeepLException("DeepL 暂时不可用，请稍后重试。");
                        try
                        {
                            var item = (Dictionary<string, object>)json.DeserializeObject(await response.Content.ReadAsStringAsync());
                            var translations = (object[])item["translations"];
                            result = (string)((Dictionary<string, object>)translations[0])["text"];
                            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException();
                        }
                        catch { ct.ThrowIfCancellationRequested(); throw new DeepLException("DeepL 返回了无法解析的译文，请稍后重试。"); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                throw new DeepLException("DeepL 请求超时，请稍后重试。");
            }
            catch (HttpRequestException) { throw new DeepLException("无法连接 DeepL，请检查网络后重试。"); }
            ct.ThrowIfCancellationRequested();
            // Clearing/replacing credentials while a request is in flight also discards its result.
            if (scope != Scope(credentials.Read())) throw new OperationCanceledException();
            if (cache.Count >= 256) cache.Clear();
            cache[cacheKey] = result;
            return result;
        }
        public void Dispose() { http.Dispose(); }
    }
}
