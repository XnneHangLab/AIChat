using AIChat.Core;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AIChat.Services
{
    public static class TTSClient
    {
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
            if (audioPathCheck && !File.Exists(refPath))
            {
                string defaultPath = Path.Combine(BepInEx.Paths.PluginPath, "ChillAIMod", "Voice.wav");
                if (File.Exists(defaultPath)) refPath = defaultPath;
                else
                {
                    logger.LogError($"[TTS] 找不到参考音频: {refPath}");
                    onComplete?.Invoke(null);
                    yield break;
                }
            }

            string jsonBody = "{ "
                + "\"text\": \"" + ResponseParser.EscapeJson(textToSpeak) + "\", "
                + "\"text_lang\": \"" + targetLang + "\", "
                + "\"ref_audio_path\": \"" + ResponseParser.EscapeJson(refPath) + "\", "
                + "\"prompt_text\": \"" + ResponseParser.EscapeJson(promptText) + "\", "
                + "\"prompt_lang\": \"" + promptLang + "\" }";

            logger.LogInfo($"[TTS] 完整请求信息:");
            logger.LogInfo($"[TTS]   URL: {url}");
            logger.LogInfo($"[TTS]   Request Body: {jsonBody}");
            logger.LogInfo("[TTS] 开始生成语音...");

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.WAV);
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = (int)timeoutSeconds;

                    var requestStartTime = DateTime.UtcNow;
                    yield return request.SendWebRequest();
                    var requestDuration = (DateTime.UtcNow - requestStartTime).TotalSeconds;

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        var clip = DownloadHandlerAudioClip.GetContent(request);
                        if (clip != null)
                        {
                            logger.LogInfo($"[TTS] 语音生成成功（第 {attempt} 次尝试）（耗时 {requestDuration:F2}s）");
                            onComplete?.Invoke(clip);
                            yield break;
                        }
                    }

                    logger.LogWarning($"[TTS] 第 {attempt}/{maxRetries} 次尝试失败（耗时 {requestDuration:F2}s）: {request.error}");
                    if (attempt < maxRetries)
                        yield return new WaitForSeconds(2f);
                }
            }

            logger.LogError("[TTS] 所有重试均失败，放弃生成语音");
            onComplete?.Invoke(null);
        }

        /// <summary>
        /// 双向 TTS 心跳循环：未连接时 5s 轮询，已连接后 30s 轮询。
        /// 直接内联检测逻辑，避免每次 StartCoroutine 分配额外对象。
        /// </summary>
        public static IEnumerator TTSHealthLoop(
            Func<string> getBaseUrl,
            ManualLogSource logger,
            Action<bool> onStateChanged)
        {
            // 缓存 WaitForSeconds 对象，避免每次循环 new 分配
            var waitShort = new WaitForSeconds(5f);  // 未连接时
            var waitLong  = new WaitForSeconds(30f); // 已连接时

            bool lastState = false;
            // 探测包：POST 最小 JSON，不读 body（只看连接状态）
            byte[] probeBody = Encoding.UTF8.GetBytes("{\"text\": \"test\"}");

            while (true)
            {
                string ttsUrl = getBaseUrl().TrimEnd('/') + "/tts";
                bool isReady = false;

                using (UnityWebRequest req = new UnityWebRequest(ttsUrl, "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(probeBody);
                    // 不设 downloadHandler：不读 body，减少内存分配
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = 5;

                    yield return req.SendWebRequest();

                    isReady = req.result == UnityWebRequest.Result.Success
                           || req.responseCode == 422
                           || req.responseCode == 400;
                }

                // 只在状态变化时回调 + 打 Log，避免每次都刷日志
                if (isReady != lastState)
                {
                    lastState = isReady;
                    onStateChanged?.Invoke(isReady);
                    if (isReady)
                        logger.LogInfo("[TTS Health] 服务已连接 ✅");
                    else
                        logger.LogWarning("[TTS Health] 服务断开 ❌");
                }

                yield return isReady ? waitLong : waitShort;
            }
        }
    }
}
