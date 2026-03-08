using AIChat.Core;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AIChat.Services
{
    public static class TTSClient
    {
        public enum Provider
        {
            GptSovits,
            FasterQwenTts,
        }

        private static readonly HttpClient HttpClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            UseProxy = false,
        });

        public sealed class StreamingAudioPlayer
        {
            private readonly object _lock = new object();
            private readonly Queue<float> _pcmQueue = new Queue<float>();
            private long _totalAppendedSamples = 0;
            private long _totalConsumedSamples = 0;
            private int _sampleRate = 0;
            private int _channels = 0;
            private bool _initialized = false;
            private bool _completed = false;
            private string _errorMessage = null;

            public int SampleRate
            {
                get
                {
                    lock (_lock)
                    {
                        return _sampleRate;
                    }
                }
            }

            public int Channels
            {
                get
                {
                    lock (_lock)
                    {
                        return _channels;
                    }
                }
            }

            public bool Initialized
            {
                get
                {
                    lock (_lock)
                    {
                        return _initialized;
                    }
                }
            }

            public bool Completed
            {
                get
                {
                    lock (_lock)
                    {
                        return _completed;
                    }
                }
            }

            public string ErrorMessage
            {
                get
                {
                    lock (_lock)
                    {
                        return _errorMessage;
                    }
                }
            }
            public long TotalAppendedSamples
            {
                get
                {
                    lock (_lock)
                    {
                        return _totalAppendedSamples;
                    }
                }
            }
            public long TotalConsumedSamples
            {
                get
                {
                    lock (_lock)
                    {
                        return _totalConsumedSamples;
                    }
                }
            }

            public int BufferedSamples
            {
                get
                {
                    lock (_lock)
                    {
                        return _pcmQueue.Count;
                    }
                }
            }

            public float BufferedSeconds
            {
                get
                {
                    lock (_lock)
                    {
                        if (!_initialized || _sampleRate <= 0 || _channels <= 0)
                            return 0f;
                        return _pcmQueue.Count / (float)(_sampleRate * _channels);
                    }
                }
            }

            public void SetFormat(int sampleRate, int channels)
            {
                lock (_lock)
                {
                    if (!_initialized)
                    {
                        _sampleRate = sampleRate;
                        _channels = channels;
                        _initialized = true;
                        return;
                    }

                    if (_sampleRate != sampleRate || _channels != channels)
                        throw new InvalidOperationException("stream sample format changed unexpectedly");
                }
            }

            public void AppendSamples(float[] samples)
            {
                if (samples == null || samples.Length == 0)
                    return;

                lock (_lock)
                {
                    for (int i = 0; i < samples.Length; i++)
                        _pcmQueue.Enqueue(samples[i]);
                    _totalAppendedSamples += samples.Length;
                }
            }

            public void Read(float[] output)
            {
                lock (_lock)
                {
                    int i = 0;
                    while (i < output.Length && _pcmQueue.Count > 0)
                    {
                        output[i++] = _pcmQueue.Dequeue();
                        _totalConsumedSamples++;
                    }

                    while (i < output.Length)
                    {
                        output[i++] = 0f;
                    }
                }
            }

            public void MarkCompleted()
            {
                lock (_lock)
                {
                    _completed = true;
                }
            }

            public void MarkError(string message)
            {
                lock (_lock)
                {
                    _errorMessage = message;
                    _completed = true;
                }
            }
        }

        public static Provider GetProvider(bool useGptSovits, bool useFasterQwenTts)
        {
            if (useFasterQwenTts && !useGptSovits)
                return Provider.FasterQwenTts;
            return Provider.GptSovits;
        }

        public static string GetGptSovitsEndpoint(string baseUrl)
        {
            return baseUrl.TrimEnd('/') + "/tts/gptsovitsv2/tts";
        }

        public static string NormalizeQwenTtsBase(string baseUrl)
        {
            string normalized = baseUrl.TrimEnd('/');
            if (normalized.EndsWith("/tts/qwen-tts", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return normalized + "/tts/qwen-tts";
        }

        public static string GetQwenTtsGenerateEndpoint(string baseUrl)
        {
            return NormalizeQwenTtsBase(baseUrl) + "/generate";
        }

        public static string GetQwenTtsStreamEndpoint(string baseUrl)
        {
            return NormalizeQwenTtsBase(baseUrl) + "/generate/stream";
        }

        public static string GetQwenTtsHealthEndpoint(string baseUrl)
        {
            return NormalizeQwenTtsBase(baseUrl) + "/health";
        }

        public static string ResolveReferenceAudioPath(string refPath, bool audioPathCheck)
        {
            if (string.IsNullOrWhiteSpace(refPath))
                return refPath;

            if (Path.IsPathRooted(refPath) && File.Exists(refPath))
                return refPath;

            if (File.Exists(refPath))
                return Path.GetFullPath(refPath);

            string pluginPath = Path.Combine(BepInEx.Paths.PluginPath, "ChillAIMod", Path.GetFileName(refPath));
            if (File.Exists(pluginPath))
                return pluginPath;

            if (audioPathCheck)
            {
                string defaultPath = Path.Combine(BepInEx.Paths.PluginPath, "ChillAIMod", "Voice.wav");
                if (File.Exists(defaultPath))
                    return defaultPath;
            }

            return refPath;
        }

        public static IEnumerator DownloadVoiceWithRetry(
            string url,
            string textToSpeak,
            string targetLang,
            string refPath,
            string promptText,
            string promptLang,
            ManualLogSource logger,
            Action<AudioClip> onComplete,
            int maxRetries = 3,
            float timeoutSeconds = 30f,
            bool audioPathCheck = false)
        {
            refPath = ResolveReferenceAudioPath(refPath, audioPathCheck);
            if (audioPathCheck && !File.Exists(refPath))
            {
                logger.LogError($"[TTS] 找不到参考音频: {refPath}");
                onComplete?.Invoke(null);
                yield break;
            }

            string requestUrl = url
                + "?text=" + UnityWebRequest.EscapeURL(textToSpeak ?? string.Empty)
                + "&text_lang=" + UnityWebRequest.EscapeURL(targetLang ?? string.Empty)
                + "&ref_audio_path=" + UnityWebRequest.EscapeURL(refPath ?? string.Empty)
                + "&prompt_text=" + UnityWebRequest.EscapeURL(promptText ?? string.Empty)
                + "&prompt_lang=" + UnityWebRequest.EscapeURL(promptLang ?? string.Empty)
                + "&speed_factor=1.0";

            logger.LogInfo("[TTS] 完整请求信息:");
            logger.LogInfo($"[TTS]   URL: {requestUrl}");

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(requestUrl, AudioType.WAV))
                {
                    request.timeout = (int)timeoutSeconds;

                    var requestStartTime = DateTime.UtcNow;
                    yield return request.SendWebRequest();
                    var requestDuration = (DateTime.UtcNow - requestStartTime).TotalSeconds;

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        var clip = DownloadHandlerAudioClip.GetContent(request);
                        if (clip != null)
                        {
                            logger.LogInfo($"[TTS] 语音生成成功（第 {attempt} 次尝试，耗时 {requestDuration:F2}s）");
                            onComplete?.Invoke(clip);
                            yield break;
                        }
                    }

                    logger.LogWarning($"[TTS] 第 {attempt}/{maxRetries} 次尝试失败（耗时 {requestDuration:F2}s）：{request.error}");
                    if (attempt < maxRetries)
                        yield return new WaitForSeconds(1f);
                }
            }

            logger.LogError("[TTS] 所有重试均失败，放弃生成语音");
            onComplete?.Invoke(null);
        }

        public static IEnumerator DownloadQwenVoiceWithRetry(
            string baseUrl,
            string textToSpeak,
            string refPath,
            string promptText,
            ManualLogSource logger,
            Action<AudioClip> onComplete,
            int maxRetries = 3,
            float timeoutSeconds = 30f,
            bool audioPathCheck = false)
        {
            string resolvedRefPath = ResolveReferenceAudioPath(refPath, audioPathCheck);
            byte[] refAudioBytes = null;
            string refAudioName = null;

            if (!string.IsNullOrWhiteSpace(resolvedRefPath) && File.Exists(resolvedRefPath))
            {
                refAudioBytes = File.ReadAllBytes(resolvedRefPath);
                refAudioName = Path.GetFileName(resolvedRefPath);
            }

            string url = GetQwenTtsGenerateEndpoint(baseUrl);

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                WWWForm form = new WWWForm();
                form.AddField("text", textToSpeak ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(promptText))
                    form.AddField("ref_text", promptText);
                if (refAudioBytes != null)
                    form.AddBinaryData("ref_audio", refAudioBytes, refAudioName ?? "ref.wav", "audio/wav");

                using (UnityWebRequest request = UnityWebRequest.Post(url, form))
                {
                    request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.WAV);
                    request.timeout = (int)timeoutSeconds;

                    var requestStartTime = DateTime.UtcNow;
                    yield return request.SendWebRequest();
                    var requestDuration = (DateTime.UtcNow - requestStartTime).TotalSeconds;

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        var clip = DownloadHandlerAudioClip.GetContent(request);
                        if (clip != null)
                        {
                            logger.LogInfo($"[Qwen-TTS] 语音生成成功（第 {attempt} 次尝试，耗时 {requestDuration:F2}s）");
                            onComplete?.Invoke(clip);
                            yield break;
                        }
                    }

                    logger.LogWarning($"[Qwen-TTS] 第 {attempt}/{maxRetries} 次尝试失败（耗时 {requestDuration:F2}s）：{request.error}");
                    if (attempt < maxRetries)
                        yield return new WaitForSeconds(1f);
                }
            }

            logger.LogError("[Qwen-TTS] 所有重试均失败，放弃生成语音");
            onComplete?.Invoke(null);
        }

        public static Task StreamQwenTtsAsync(
            string baseUrl,
            string textToSpeak,
            string refPath,
            string promptText,
            StreamingAudioPlayer player,
            ManualLogSource logger,
            CancellationToken cancellationToken,
            bool audioPathCheck = false,
            string requestLabel = null)
        {
            return Task.Run(async () =>
            {
                try
                {
                    string label = string.IsNullOrWhiteSpace(requestLabel) ? "Qwen-TTS" : requestLabel;
                    string resolvedRefPath = ResolveReferenceAudioPath(refPath, audioPathCheck);
                    using (var content = new MultipartFormDataContent())
                    {
                        content.Add(new StringContent(textToSpeak ?? string.Empty, Encoding.UTF8), "text");
                        if (!string.IsNullOrWhiteSpace(promptText))
                            content.Add(new StringContent(promptText, Encoding.UTF8), "ref_text");
                        if (!string.IsNullOrWhiteSpace(resolvedRefPath) && File.Exists(resolvedRefPath))
                        {
                            var audioContent = new ByteArrayContent(File.ReadAllBytes(resolvedRefPath));
                            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                            content.Add(audioContent, "ref_audio", Path.GetFileName(resolvedRefPath));
                        }

                        using (var request = new HttpRequestMessage(HttpMethod.Post, GetQwenTtsStreamEndpoint(baseUrl)))
                        {
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                            request.Content = content;
                            logger.LogInfo($"[{label}] 流式请求发出");

                            using (HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                            {
                                response.EnsureSuccessStatusCode();
                                logger.LogInfo($"[{label}] 已建立 SSE 连接");

                                using (Stream stream = await response.Content.ReadAsStreamAsync())
                                using (var reader = new StreamReader(stream, Encoding.UTF8))
                                {
                                    var dataLines = new List<string>();
                                    while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                                    {
                                        string line = await reader.ReadLineAsync();
                                        if (line == null)
                                            break;
                                        if (string.IsNullOrEmpty(line))
                                        {
                                            if (dataLines.Count > 0)
                                            {
                                                HandleQwenSseEvent(string.Join("\n", dataLines), player, logger, label);
                                                dataLines.Clear();
                                            }
                                            continue;
                                        }

                                        if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                                            dataLines.Add(line.Substring(5).TrimStart());
                                    }

                                    if (dataLines.Count > 0)
                                        HandleQwenSseEvent(string.Join("\n", dataLines), player, logger, label);
                                }
                            }
                        }
                    }

                    logger.LogInfo($"[{label}] 流式请求结束");
                    player.MarkCompleted();
                }
                catch (OperationCanceledException)
                {
                    string label = string.IsNullOrWhiteSpace(requestLabel) ? "Qwen-TTS" : requestLabel;
                    logger.LogInfo($"[{label}] 流式请求被取消");
                    player.MarkCompleted();
                }
                catch (Exception ex)
                {
                    string label = string.IsNullOrWhiteSpace(requestLabel) ? "Qwen-TTS" : requestLabel;
                    if (player.Completed)
                    {
                        logger.LogInfo($"[{label}] 流在完成后关闭：{ex.Message}");
                        player.MarkCompleted();
                        return;
                    }
                    logger.LogWarning($"[{label}] 流式请求失败：{ex.Message}");
                    player.MarkError(ex.Message);
                }
            }, cancellationToken);
        }

        private static void HandleQwenSseEvent(string jsonPayload, StreamingAudioPlayer player, ManualLogSource logger, string label)
        {
            string eventType = ResponseParser.ExtractJsonValue(jsonPayload, "type");
            if (string.Equals(eventType, "chunk", StringComparison.OrdinalIgnoreCase))
            {
                string audioB64 = ResponseParser.ExtractJsonValue(jsonPayload, "audio_b64");
                if (string.IsNullOrEmpty(audioB64))
                    return;

                byte[] wavBytes = Convert.FromBase64String(audioB64);
                if (!AudioUtils.TryDecodeWavToFloat(wavBytes, out float[] samples, out int sampleRate, out int channels))
                    throw new InvalidDataException("failed to decode qwen-tts wav chunk");

                if (!player.Initialized)
                    logger.LogInfo($"[{label}] 收到首个 chunk");
                player.SetFormat(sampleRate, channels);
                player.AppendSamples(samples);
                return;
            }

            if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            {
                string message = ResponseParser.ExtractJsonValue(jsonPayload, "message");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "unknown qwen-tts stream error" : message);
            }

            if (string.Equals(eventType, "done", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInfo($"[{label}] 收到 done");
                player.MarkCompleted();
            }
        }

        public static IEnumerator TTSHealthLoop(
            Func<string> getBaseUrl,
            Func<Provider> getProvider,
            ManualLogSource logger,
            Action<bool> onStateChanged)
        {
            var waitStep = new WaitForSeconds(1f);
            bool lastState = false;
            Provider? lastProvider = null;

            while (true)
            {
                Provider provider = getProvider();
                if (lastProvider.HasValue && lastProvider.Value != provider)
                {
                    lastState = false;
                    onStateChanged?.Invoke(false);
                    logger.LogInfo($"[TTS Health] 切换 provider => {provider}，立即重新探测");
                }

                string ttsUrl = provider == Provider.FasterQwenTts
                    ? GetQwenTtsHealthEndpoint(getBaseUrl())
                    : GetGptSovitsEndpoint(getBaseUrl());
                bool isReady = false;

                string healthUrl = provider == Provider.GptSovits
                    ? ttsUrl + "?text=test&text_lang=ja&ref_audio_path=elaina.wav&prompt_text=test&prompt_lang=ja&speed_factor=1.0"
                    : ttsUrl;

                using (UnityWebRequest req = UnityWebRequest.Get(healthUrl))
                {
                    req.timeout = 5;

                    yield return req.SendWebRequest();

                    if (provider == Provider.FasterQwenTts)
                    {
                        isReady = req.result == UnityWebRequest.Result.Success && req.responseCode == 200;
                    }
                    else
                    {
                        isReady = req.result == UnityWebRequest.Result.Success
                            || req.responseCode == 422
                            || req.responseCode == 400;
                    }
                }

                if (isReady != lastState)
                {
                    lastState = isReady;
                    onStateChanged?.Invoke(isReady);
                    if (isReady)
                        logger.LogInfo($"[TTS Health] {provider} 服务已连接");
                    else
                        logger.LogWarning($"[TTS Health] {provider} 服务断开");
                }

                lastProvider = provider;

                int waitSeconds = isReady ? 30 : 5;
                for (int i = 0; i < waitSeconds; i++)
                {
                    if (getProvider() != provider)
                        break;
                    yield return waitStep;
                }
            }
        }
    }
}
