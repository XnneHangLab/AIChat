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
        public bool UseXnneHangLabChatServer;
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
            bool useXnneHangLabChatServer = false,
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
            UseXnneHangLabChatServer = useXnneHangLabChatServer;
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

            // 1) JSON 格式：{"emotion":"Happy","voice_text":"...","subtitle_text":"..."}
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

            // 2) 标准 ||| 格式
            // [Emotion] ||| Subtitle
            // [Emotion] ||| Subtitle ||| Voice
            string[] parts = response.Split(new string[] { "|||" }, StringSplitOptions.None);

            if (parts.Length == 2)
            {
                ret.EmotionTag = parts[0].Trim().Replace("[", "").Replace("]", "");
                ret.SubtitleText = parts[1].Trim();
                ret.VoiceText = parts[1].Trim();
                ret.Success = true;
                Log.Info("[解析] 2段格式解析成功（翻译模式）");
                return ret;
            }

            if (parts.Length == 3)
            {
                ret.EmotionTag = parts[0].Trim().Replace("[", "").Replace("]", "");
                ret.SubtitleText = parts[1].Trim();
                ret.VoiceText = parts[2].Trim();
                ret.Success = true;
                Log.Info("[解析] 3段格式解析成功（双语模式）");
                return ret;
            }

            // 3) 多情绪块格式（同一次回复里出现多个 [Emotion] ||| 段）
            var tagMatches = System.Text.RegularExpressions.Regex.Matches(
                response,
                @"\[(?<emotion>[^\]\r\n]+)\]\s*\|\|\|\s*",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            if (tagMatches.Count >= 2)
            {
                ret.EmotionTag = tagMatches[0].Groups["emotion"].Value.Trim();
                var chunks = new List<string>();

                for (int i = 0; i < tagMatches.Count; i++)
                {
                    int start = tagMatches[i].Index + tagMatches[i].Length;
                    int end = (i + 1 < tagMatches.Count) ? tagMatches[i + 1].Index : response.Length;
                    int len = end - start;
                    if (len <= 0) continue;

                    string chunk = response.Substring(start, len).Trim();
                    if (!string.IsNullOrEmpty(chunk))
                        chunks.Add(chunk);
                }

                if (chunks.Count > 0)
                {
                    string mergedText = string.Join("\n\n", chunks);
                    ret.SubtitleText = mergedText;
                    ret.VoiceText = mergedText;
                    ret.Success = true;
                    Log.Info($"[解析] 多情绪分段解析成功（{chunks.Count} 段）");
                    return ret;
                }
            }

            // 4) 兜底：尽力剥离首个 [Emotion] ||| 前缀，其余保留
            Log.Warning($"[解析] 格式异常（{parts.Length} 段），完整内容：{response}");
            string fallbackText = response;
            var emotionMatch = System.Text.RegularExpressions.Regex.Match(
                response,
                @"^\[([^\]]+)\]\s*\|*\|*\|*\s*(.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (emotionMatch.Success)
            {
                ret.EmotionTag = emotionMatch.Groups[1].Value.Trim();
                fallbackText = emotionMatch.Groups[2].Value.Trim();
            }

            ret.SubtitleText = fallbackText;
            ret.VoiceText = fallbackText;
            Log.Warning($"[格式错误] AI 回复不符合格式：{response}");
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

            // XnneHangLab Chat Server：/memory/chat 端点格式
            // 新增：UseXnneHangLabChatServer 是独立配置，UseXnneHangLab 是旧配置
            if (requestContext.UseXnneHangLabChatServer || requestContext.UseXnneHangLab)
            {
                // 检查 URL 是否包含 /memory/chat
                bool useChatEndpoint = requestContext.ApiUrl.Contains("/memory/chat");
                
                if (useChatEndpoint)
                {
                    // /memory/chat 端点：system prompt 由 server 端生成，不需要客户端发送
                    // 当使用 UseXnneHangLabChatServer 时，后端已配置好所有模型和 API key，客户端只发送 message
                    string sessionParam = ""; // 可以后续扩展支持 session_id
                    
                    // 仅在使用旧配置 (UseXnneHangLab) 且 ModelName 不为空时，才发送 model 参数
                    string modelParam = "";
                    if (requestContext.UseXnneHangLab && !string.IsNullOrEmpty(requestContext.ModelName))
                    {
                        modelParam = ", \"model\": \"" + requestContext.ModelName + "\"";
                    }
                    
                    jsonBody = "{ \"message\": \"" + ResponseParser.EscapeJson(userPromptWithMemory) + "\"" + sessionParam + modelParam + " }";
                }
                else
                {
                    // /v1/chat/completions 端点：OpenAI 兼容格式（旧版，保留向后兼容）
                    string systemContent = ResponseParser.EscapeJson(requestContext.SystemPrompt);
                    string userContent = ResponseParser.EscapeJson(userPromptWithMemory);

                    string extraParams = "";
                    if (requestContext.EnableTranslation)
                    {
                        extraParams = ", \"extra_params\": { \"translate_to\": \"" + requestContext.TranslateTargetLang + "\", \"return_format\": \"json\", \"emotion_detection\": true }";
                    }

                    jsonBody = "{ \"messages\": [ { \"role\": \"system\", \"content\": \"" + systemContent + "\" }, { \"role\": \"user\", \"content\": \"" + userContent + "\" } ]" + extraParams + " }";
                }
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
