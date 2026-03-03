using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using ChillAIMod;
using AIChat.Core;

namespace AIChat.Utils
{
    public enum ThinkMode { Default, Enable, Disable }

    public struct LLMRequestContext
    {
        public string ApiUrl;
        public string ApiKey;
        public string ModelName;
        public string SystemPrompt;
        public string UserPrompt;
        public bool UseLocalOllama;
        public bool UseXnneHangLab;
        public bool LogApiRequestBody;
        public bool FixApiPathForThinkMode;
        public ThinkMode ThinkMode;
        public HierarchicalMemory HierarchicalMemory;
        public string LogHeader;

        // --- 翻译相关配置 ---
        public bool EnableTranslation;
        public string DeepLXUrl;
        public string TranslateTargetLang;

        public LLMRequestContext(
            string apiUrl = "",
            string apiKey = "",
            string modelName = "",
            string systemPrompt = "",
            string userPrompt = "",
            bool useLocalOllama = false,
            bool useXnneHangLab = false,
            bool logApiRequestBody = false,
            ThinkMode thinkMode = ThinkMode.Default,
            HierarchicalMemory hierarchicalMemory = null,
            string logHeader = "LLMRequest",
            bool fixApiPathForThinkMode = false,
            bool enableTranslation = false,
            string deeplxUrl = "",
            string translateTargetLang = "ZH"
        )
        {
            ApiUrl = apiUrl;
            ApiKey = apiKey;
            ModelName = modelName;
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            UseLocalOllama = useLocalOllama;
            UseXnneHangLab = useXnneHangLab;
            LogApiRequestBody = logApiRequestBody;
            ThinkMode = thinkMode;
            HierarchicalMemory = hierarchicalMemory;
            LogHeader = logHeader;
            FixApiPathForThinkMode = fixApiPathForThinkMode;
            EnableTranslation = enableTranslation;
            DeepLXUrl = deeplxUrl;
            TranslateTargetLang = translateTargetLang;
        }
    }

    public struct LLMStandardResponse
    {
        public bool Success;
        public string EmotionTag;   // 动作标签，如 [Happy], [Think] 等
        public string VoiceText;    // 用于 TTS 的文本
        public string SubtitleText; // 用于字幕显示的文本

        public LLMStandardResponse(bool success, string emotionTag, string voiceText, string subtitleText)
        {
            Success = success;
            EmotionTag = emotionTag;
            VoiceText = voiceText;
            SubtitleText = subtitleText;
        }
    }

    public static class LLMUtils
    {
        public static LLMStandardResponse ParseStandardResponse(string response)
        {
            LLMStandardResponse ret = new LLMStandardResponse(false, "Think", "", response);

            // ================= 【尝试 JSON 格式解析】 =================
            // 期望格式：{"emotion": "Happy", "voice_text": "こんにちは", "subtitle_text": "你好"}
            if (response.Trim().StartsWith("{"))
            {
                try
                {
                    string emotion = ExtractJsonValue(response, "emotion");
                    string voiceText = ExtractJsonValue(response, "voice_text");
                    string subtitleText = ExtractJsonValue(response, "subtitle_text");

                    if (!string.IsNullOrEmpty(emotion) && !string.IsNullOrEmpty(voiceText))
                    {
                        ret.EmotionTag = emotion.Trim().Replace("[", "").Replace("]", "");
                        ret.VoiceText = voiceText.Trim();
                        ret.SubtitleText = !string.IsNullOrEmpty(subtitleText) ? subtitleText.Trim() : voiceText.Trim();
                        ret.Success = true;
                        Log.Info("[解析] JSON 格式解析成功");
                        return ret;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[解析] JSON 解析失败：{ex.Message}");
                }
            }

            // ================= 【||| 格式解析】 =================
            // 字段顺序：[Emotion] ||| SubtitleText (||| VoiceText)
            // 2段：[Emotion] ||| 字幕文本        → VoiceText = SubtitleText（配合翻译模式）
            // 3段：[Emotion] ||| 字幕文本 ||| TTS文本  → 双语兼容模式
            // 1段或>3段：格式异常，打印完整内容供调试
            string[] parts = response.Split(new string[] { "|||" }, StringSplitOptions.None);

            if (parts.Length == 2)
            {
                // 标准翻译模式：[Emotion] ||| 中文字幕
                ret.EmotionTag = parts[0].Trim().Replace("[", "").Replace("]", "");
                ret.SubtitleText = parts[1].Trim();
                ret.VoiceText = parts[1].Trim(); // TTS 用原文，翻译由 DeepLX 负责
                ret.Success = true;
                Log.Info("[解析] 2段格式解析成功（翻译模式）");
            }
            else if (parts.Length == 3)
            {
                // 双语兼容模式：[Emotion] ||| 字幕文本 ||| TTS文本
                ret.EmotionTag = parts[0].Trim().Replace("[", "").Replace("]", "");
                ret.SubtitleText = parts[1].Trim();
                ret.VoiceText = parts[2].Trim();
                ret.Success = true;
                Log.Info("[解析] 3段格式解析成功（双语模式）");
            }
            else
            {
                // 格式异常：1段或>3段，打印完整内容便于调试
                Log.Warning($"[解析] 格式异常（{parts.Length} 段），完整内容：{response}");
                ret.SubtitleText = response;
                ret.VoiceText = response;
            }

            if (!ret.Success) Log.Warning($"[格式错误] AI 回复不符合格式：{response}");

            return ret;
        }

        /// <summary>
        /// 简单的 JSON 字符串值提取（不依赖第三方库）
        /// </summary>
        private static string ExtractJsonValue(string json, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey);
            if (keyIndex == -1) return null;

            int colonIndex = json.IndexOf(':', keyIndex);
            if (colonIndex == -1) return null;

            int startIndex = colonIndex + 1;
            while (startIndex < json.Length && char.IsWhiteSpace(json[startIndex]))
                startIndex++;

            if (startIndex >= json.Length) return null;

            if (json[startIndex] == '"')
            {
                startIndex++; // 跳过开头引号
                int endIndex = json.IndexOf('"', startIndex);
                if (endIndex == -1) return null;
                return json.Substring(startIndex, endIndex - startIndex);
            }
            else
            {
                int endIndex = startIndex;
                while (endIndex < json.Length && json[endIndex] != ',' && json[endIndex] != '}' && json[endIndex] != ']')
                    endIndex++;
                return json.Substring(startIndex, endIndex - startIndex).Trim();
            }
        }

        public static string BuildRequestBody(LLMRequestContext requestContext)
        {
            string userPromptWithMemory = GetContextWithMemory(requestContext.HierarchicalMemory, requestContext.UserPrompt);

            string jsonBody;

            // XnneHangLab Chat Server：简单的 OpenAI 兼容格式
            if (requestContext.UseXnneHangLab)
            {
                string systemContent = ResponseParser.EscapeJson(requestContext.SystemPrompt);
                string userContent = ResponseParser.EscapeJson(userPromptWithMemory);

                string extraParams = "";
                if (requestContext.EnableTranslation)
                {
                    extraParams = ", \"extra_params\": { \"translate_to\": \"" + requestContext.TranslateTargetLang + "\", \"return_format\": \"json\", \"emotion_detection\": true }";
                }

                jsonBody = "{ \"messages\": [ { \"role\": \"system\", \"content\": \"" + systemContent + "\" }, { \"role\": \"user\", \"content\": \"" + userContent + "\" } ]" + extraParams + " }";
            }
            else
            {
                string extraJson = requestContext.UseLocalOllama ? ",\"stream\": false" : "";
                extraJson += GetThinkParameterJson(requestContext.ThinkMode);

                string systemContent = ResponseParser.EscapeJson(requestContext.SystemPrompt);
                string userContent = ResponseParser.EscapeJson(userPromptWithMemory);

                if (requestContext.ModelName.Contains("gemma"))
                {
                    string finalPrompt = ResponseParser.EscapeJson("[System Instruction]\n" + requestContext.SystemPrompt + "\n\n[User Message]\n" + userPromptWithMemory);
                    jsonBody = "{ \"model\": \"" + requestContext.ModelName + "\", \"messages\": [ { \"role\": \"user\", \"content\": \"" + finalPrompt + "\" } ]" + extraJson + " }";
                }
                else
                {
                    jsonBody = "{ \"model\": \"" + requestContext.ModelName + "\", \"messages\": [ { \"role\": \"system\", \"content\": \"" + systemContent + "\" }, { \"role\": \"user\", \"content\": \"" + userContent + "\" } ]" + extraJson + " }";
                }
            }

            Log.Info($"[记忆系统] 启用状态：{requestContext.HierarchicalMemory != null}");
            if (requestContext.LogApiRequestBody)
            {
                Log.Info($"[发送给 LLM 的完整内容]\n========================================\n[System Prompt]\n{requestContext.SystemPrompt}\n\n[User Content]\n{userPromptWithMemory}\n========================================");
                Log.Info($"[API 请求] 完整请求体:\n{jsonBody}");
            }

            return jsonBody;
        }

        /// <summary>
        /// 获取深度思考参数的 JSON 字符串
        /// </summary>
        private static string GetThinkParameterJson(ThinkMode thinkMode)
        {
            if (thinkMode == ThinkMode.Enable)
                return ",\"think\": true";
            else if (thinkMode == ThinkMode.Disable)
                return ",\"think\": false";
            return "";
        }

        private static string GetContextWithMemory(HierarchicalMemory hierarchicalMemory, string currentPrompt)
        {
            if (hierarchicalMemory != null)
            {
                string memoryContext = hierarchicalMemory.GetContext();
                Log.Info($"[记忆系统] 当前记忆状态:\n{hierarchicalMemory.GetMemoryStats()}");
                if (!string.IsNullOrWhiteSpace(memoryContext))
                    return $"{memoryContext}\n\n【Current Input】\n{currentPrompt}";
            }
            return currentPrompt;
        }

        /// <summary>
        /// 获取适合当前 think 模式的 API URL
        /// </summary>
        public static string GetApiUrlForThinkMode(LLMRequestContext requestContext)
        {
            string baseUrl = requestContext.ApiUrl;
            if (requestContext.UseXnneHangLab)
                return baseUrl;

            if (requestContext.FixApiPathForThinkMode && requestContext.ThinkMode != ThinkMode.Default)
            {
                if (baseUrl.Contains("/v1/chat/completions"))
                {
                    baseUrl = baseUrl.Replace("/v1/chat/completions", "/api/chat");
                    Log.Info($"[Think Mode] 切换到 Ollama 原生 API: {baseUrl}");
                }
            }

            return baseUrl;
        }
    }
}
