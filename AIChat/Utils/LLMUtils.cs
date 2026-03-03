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
        public string DeepLX_Url;
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
            DeepLX_Url = deeplxUrl;
            TranslateTargetLang = translateTargetLang;
        }
    }

    public struct LLMStandardResponse
    {
        public bool Success;
        public string EmotionTag;  // 动作标签，如 [Happy], [Think] 等
        public string VoiceText;   // 用于 TTS 的文本
        public string SubtitleText;// 用于字幕显示的文本

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
                    // 简单的 JSON 解析（不使用第三方库）
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
            
            // ================= 【回退到 ||| 格式解析】 =================
            // 按 ||| 分割（注意：有些模型可能会用单个 | ）
            string[] parts = response.Split(new string[] { "|||" }, StringSplitOptions.None);

            // 如果不是 |||，尝试单个 |
            if (parts.Length < 3)
            {
                parts = response.Split(new string[] { "|" }, StringSplitOptions.None);
            }

            // 【核心修改：严格的格式检查】
            if (parts.Length >= 3)
            {
                // 格式正确：[动作] ||| 日语 ||| 中文
                ret.EmotionTag = parts[0].Trim().Replace("[", "").Replace("]", "");
                ret.VoiceText = parts[1].Trim();
                ret.SubtitleText = parts[2].Trim();

                ret.Success = true;
            }

            if (!ret.Success) Log.Warning($"[格式错误] AI 回复不符合格式：{response}");

            return ret;
        }
        
        /// <summary>
        /// 简单的 JSON 值提取（不使用第三方库）
        /// </summary>
        private static string ExtractJsonValue(string json, string key)
        {
            string searchKey = $"\"{key}\"";
            int keyIndex = json.IndexOf(searchKey);
            if (keyIndex == -1) return null;
            
            int colonIndex = json.IndexOf(':', keyIndex);
            if (colonIndex == -1) return null;
            
            // 跳过冒号后的空格
            int startIndex = colonIndex + 1;
            while (startIndex < json.Length && char.IsWhiteSpace(json[startIndex]))
            {
                startIndex++;
            }
            
            if (startIndex >= json.Length) return null;
            
            // 检查是字符串还是其他类型
            if (json[startIndex] == '"')
            {
                // 字符串值
                startIndex++; // 跳过开头的引号
                int endIndex = json.IndexOf('"', startIndex);
                if (endIndex == -1) return null;
                return json.Substring(startIndex, endIndex - startIndex);
            }
            else
            {
                // 非字符串值（数字、布尔值等）
                int endIndex = startIndex;
                while (endIndex < json.Length && json[endIndex] != ',' && json[endIndex] != '}' && json[endIndex] != ']')
                {
                    endIndex++;
                }
                return json.Substring(startIndex, endIndex - startIndex).Trim();
            }
        }

        public static string BuildRequestBody(LLMRequestContext requestContext)
        {
            // 【集成分层记忆】获取带记忆上下文的提示词
            string userPromptWithMemory = GetContextWithMemory(requestContext.HierarchicalMemory, requestContext.UserPrompt);

            string jsonBody;
            
            // XnneHangLab Chat Server：簡單的 OpenAI 兼容格式，不需要 stream 參數，也不需要 model（由後端配置）
            if (requestContext.UseXnneHangLab)
            {
                // 如果启用翻译，添加 extra_params
                string extraParams = "";
                if (requestContext.EnableTranslation)
                {
                    extraParams = $@", ""extra_params"": {{ ""translate_to"": ""{requestContext.TranslateTargetLang}"", ""return_format"": ""json"", ""emotion_detection"": true }}";
                }
                
                jsonBody = $@"{{ ""messages"": [ {{ ""role"": ""system"", ""content"": ""{ResponseParser.EscapeJson(requestContext.SystemPrompt)}"" }}, {{ ""role"": ""user"", ""content"": ""{ResponseParser.EscapeJson(userPromptWithMemory)}"" }} ]{extraParams} }}";
            }
            else
            {
                // Ollama 或其他 OpenAI 兼容 API
                string extraJson = requestContext.UseLocalOllama ? $@",""stream"": false" : "";
                // 【深度思考参数】
                extraJson += GetThinkParameterJson(requestContext.ThinkMode);

                if (requestContext.ModelName.Contains("gemma")) {
                    // 将 persona 作为背景信息放在 user 消息的最前面
                    string finalPrompt = $"[System Instruction]\n{requestContext.SystemPrompt}\n\n[User Message]\n{userPromptWithMemory}";
                    jsonBody = $@"{{ ""model"": ""{requestContext.ModelName}"", ""messages"": [ {{ ""role"": ""user"", ""content"": ""{ResponseParser.EscapeJson(finalPrompt)}"" }} ]{extraJson} }}";
                } else {
                    // Gemini 或 Ollama (如果是 Llama3 等) 通常支持 system role
                    jsonBody = $@"{{ ""model"": ""{requestContext.ModelName}"", ""messages"": [ {{ ""role"": ""system"", ""content"": ""{ResponseParser.EscapeJson(requestContext.SystemPrompt)}"" }}", {{ ""role"": ""user"", ""content"": ""{ResponseParser.EscapeJson(userPromptWithMemory)}"" }} ]{extraJson} }}";
                }
            }

            Log.Info($"[记忆系统] 启用状态：{requestContext.HierarchicalMemory != null}");
            // 【日志】打印完整的请求体（如果启用）
            if (requestContext.LogApiRequestBody)
            {
                // 【调试日志】显示完整的请求内容
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
            {
                return @",""think"": true";
            }
            else if (thinkMode == ThinkMode.Disable)
            {
                return @",""think"": false";
            }
            // Default 模式不添加 think 参数
            return "";
        }

        private static string GetContextWithMemory(HierarchicalMemory hierarchicalMemory, string currentPrompt)
        {
            if (hierarchicalMemory != null)
            {
                string memoryContext = hierarchicalMemory.GetContext();
                Log.Info($"[记忆系统] 当前记忆状态:\n{hierarchicalMemory.GetMemoryStats()}");

                // 如果有记忆内容，则拼接；否则只返回当前提示
                if (!string.IsNullOrWhiteSpace(memoryContext))
                {
                    return $"{memoryContext}\n\n【Current Input】\n{currentPrompt}";
                }
            }
            
            // 无记忆或未启用，直接返回原始 prompt
            return currentPrompt;
        }

        /// <summary>
        /// 获取适合当前 think 模式的 API URL
        /// </summary>
        public static string GetApiUrlForThinkMode(LLMRequestContext requestContext)
        {
            string baseUrl = requestContext.ApiUrl;
            // XnneHangLab Chat Server 不需要修改 URL
            if (requestContext.UseXnneHangLab)
            {
                return baseUrl;
            }
            
            // 如果启用了 API 路径修正，且 think 模式不是 Default，需要使用 Ollama 原生 API (/api/chat)
            if (requestContext.FixApiPathForThinkMode && requestContext.ThinkMode != ThinkMode.Default)
            {
                // 将 /v1/chat/completions 替换为 /api/chat
                if (baseUrl.Contains("/v1/chat/completions"))
                {
                    baseUrl = baseUrl.Replace("/v1/chat/completions", "/api/chat");
                    Log.Info($"[Think Mode] 切换到 Ollama 原生 API: {baseUrl}");
                }
                // 如果 URL 已经是 /api/chat 或其他格式，保持不变
            }
            
            return baseUrl;
        }
    }
}
