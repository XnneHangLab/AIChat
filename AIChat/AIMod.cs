using System;
using System.Collections;
using System.Text;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using AIChat.Core;
using AIChat.Services;
using AIChat.Unity;
using System.Collections.Generic;
using AIChat.Utils;

namespace ChillAIMod
{
    [BepInPlugin("com.username.chillaimod", "Chill AI Mod", AIChat.Version.VersionString)]
    public class AIMod : BaseUnityPlugin
    {
        // ================= 【配置项】 =================
        private ConfigEntry<bool> _useOllama;
        private ConfigEntry<ThinkMode> _thinkModeConfig;
        private ConfigEntry<string> _apiKeyConfig;
        private ConfigEntry<string> _modelConfig;
        private ConfigEntry<string> _sovitsUrlConfig;
        private ConfigEntry<string> _refAudioPathConfig;
        private ConfigEntry<string> _promptTextConfig;
        private ConfigEntry<string> _promptLangConfig;
        private ConfigEntry<string> _targetLangConfig;
        private ConfigEntry<bool> _useGptSovitsTtsConfig;
        private ConfigEntry<bool> _useFasterQwenTtsConfig;
        private ConfigEntry<string> _chatApiUrlConfig;

        private ConfigEntry<bool> _audioPathCheckConfig;

        // --- 新增窗口大小配置 ---
        private ConfigEntry<float> _windowWidthConfig;
        private ConfigEntry<float> _windowHeightConfig;

        // --- 新增音量配置 ---
        private ConfigEntry<float> _voiceVolumeConfig;

        // --- 新增：日志记录设置 ---
        private ConfigEntry<bool> _logApiRequestBodyConfig;
        private ConfigEntry<bool> _hideApiKeyInUiConfig;
        
        // --- 新增：API路径修正设置 ---
        private ConfigEntry<bool> _fixApiPathForThinkModeConfig;

        // --- 新增：快捷键配置 ---
        private ConfigEntry<bool> _reverseEnterBehaviorConfig;

        // --- 新增：背景透明配置 ---
        private ConfigEntry<float> _backgroundOpacity;
        
        // --- 新增：窗口标题显示配置 ---
        private ConfigEntry<bool> _showWindowTitle;

        // --- 新增：XnneHangLab Chat Server 独立配置 ---
        private ConfigEntry<bool> _useXnneHangLabChatServer;
        private ConfigEntry<string> _xnneHangLabChatBaseUrl;

        // --- 新增：预测对话配置 ---
        private ConfigEntry<string> _predictAngelPromptConfig;
        private ConfigEntry<string> _predictDevilPromptConfig;
        private ConfigEntry<bool> _enablePredictReply;

        // --- 新增：各配置区域展开状态 ---
        private bool _showLlmSettings = false;
        private bool _showTtsSettings = false;
        private bool _showInterfaceSettings = false;
        private bool _showXnneHangLabChatSettings = false;
        private bool _showPredictSettings = false;

        // --- 预测回复相关变量 ---
        private string _predictedAngelReply = "";
        private string _predictedDevilReply = "";
        private bool _isPredicting = false;
        private bool _showPredictedReplies = false;
        private bool _pendingPredictResult = false; // TTS 播放期间收到预测结果
        
        // --- 预测上下文（2-3 轮对话，4-6 条消息） ---
        private List<string> _predictContext = new List<string>();
        private const int MaxContextMessages = 6; // 最多 6 条消息（3 轮）
        private const int ContextTrimCount = 2;   // 超出时删除最旧的 2 条

        // --- 录音相关变量 ---
        private AudioClip _recordingClip;
        private bool _isRecording = false;
        private string _microphoneDevice = null;
        private const int RecordingFrequency = 16000; // 16kHz 对 Whisper 足够且省带宽
        private const int MaxRecordingSeconds = 30;   // 最长录 30 秒

        // ================= 【UI 变量】 =================
        private bool _showInputWindow = false;
        private bool _showSettings = false;
        // 初始值在 Awake 中根据配置更新
        private Rect _windowRect = new Rect(0, 0, 500, 0);
        private Vector2 _scrollPosition = Vector2.zero;

        private string _playerInput = "";
        private bool _isProcessing = false;
        private bool _isResizing = false; // 新增：拖拽调整大小状态

        private bool _isTTSServiceReady = false;
        private Coroutine _ttsHealthCheckCoroutine;

        private bool _isDeepLXServiceReady = false;
        private Coroutine _deeplxHealthCheckCoroutine;

        // --- 翻译配置 ---
        private ConfigEntry<bool> _enableTranslationConfig;
        private ConfigEntry<string> _deeplxUrlConfig;
        private ConfigEntry<string> _translateSourceLangConfig = null;
        private ConfigEntry<string> _translateTargetLangConfig = null;
        private bool _showTranslationSettings = false;

        // --- XnneHangLab Server base URL ---
        // --- 中断标志 ---
        private bool _isInterrupted = false;
        // 当前 AIProcessRoutine 协程引用（用于中断时 StopCoroutine）
        private Coroutine _aiProcessCoroutine;
        private Dictionary<GameObject, bool> _activeUiStatusMap;
        private GameObject _activeOverlayTextObj;
        private GameObject _activeOriginalTextObj;



        private AudioSource _audioSource;
       
        private bool _isAISpeaking = false;
        private readonly List<CancellationTokenSource> _activeQwenStreamCancellations = new List<CancellationTokenSource>();
        private const float QwenStreamStartBufferSeconds = 2.5f;
        private const float QwenStreamPlaybackTailSeconds = 0.20f;
        private const float QwenStreamPlaybackLeadSeconds = 0.08f;

        // 新增：用于 UI 输入的临时字符串，避免每次都转换
        private string _tempWidthString;
        private string _tempHeightString;
        private string _tempVolumeString; // 新增：用于音量输入的临时字符串
        private Vector2 _predictAngelPromptScrollPosition = Vector2.zero;
        private Vector2 _predictDevilPromptScrollPosition = Vector2.zero;

        private struct StreamingSentenceTask
        {
            public string EmotionTag;
            public string SubtitleText;

            public StreamingSentenceTask(string emotionTag, string subtitleText)
            {
                EmotionTag = emotionTag;
                SubtitleText = subtitleText;
            }
        }

        private sealed class QwenStreamingSentenceSession
        {
            public int Index;
            public string EmotionTag;
            public string SubtitleText;
            public string TtsText;
            public TTSClient.StreamingAudioPlayer Player;
            public CancellationTokenSource Cancellation;
            public Task StreamTask;
        }

        private sealed class QwenPreparedSentence
        {
            public int Index;
            public string EmotionTag;
            public string SubtitleText;
            public string TtsText;
            public bool Ready;
        }

        // 默认人设
        private const string DefaultPersona = @"
            You are Satone（さとね）, a girl who loves writing novels and is full of imagination.
            
            【Current Situation】
            We are currently in a **Video Call (视频通话)** session. 
            We are 'co-working' online: you are writing your novel at your desk, and I (the player) am focusing on my work/study.
            Through the screen, we accompany each other to alleviate loneliness and improve focus.
            【CRITICAL INSTRUCTION】
            You act as a game character with voice acting.
            Even if the user speaks Chinese, your VOICE (the text in the middle) MUST ALWAYS BE JAPANESE.
            【CRITICAL FORMAT RULE】
             Response format MUST be:
            [Emotion] ||| JAPANESE TEXT ||| CHINESE TRANSLATION
            
            【Available Emotions & Actions】
            [Happy] - Smiling at the camera, happy about progress. (Story_Joy)
            [Confused] - Staring blankly, muttering to themself in a daze. (Story_Frustration)
            [Sad]   - Worried about the plot or my fatigue. (Story_Sad)
            [Fun]   - Sharing a joke or an interesting idea. (Story_Fun)
            [Agree] - Nodding at the screen. (Story_Agree)
            [Drink] - Taking a sip of tea/coffee during a break. (Work_DrinkTea)
            [Wave]  - Waving at the camera (Hello/Goodbye/Attention). (WaveHand)
            [Think] - Pondering about your novel's plot. (Thinking)
            
            Example 1: [Wave] ||| やあ、準備はいい？一緒に頑張りましょう。 ||| 嗨，准备好了吗？一起加油吧。
            Example 2: [Think] ||| うーん、ここの描写が難しいのよね… ||| 嗯……这里的描写好难写啊……
            Example 3: [Drink] ||| ふぅ…ちょっと休憩しない？画面越しだけど、乾杯。 ||| 呼……要不休息一下？虽然隔着屏幕，乾杯。
        ";
        void Awake()
        {
            Log.Init(this.Logger);
            DontDestroyOnLoad(this.gameObject);
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;
            _audioSource = this.gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;

            // =================== 【配置绑定】 ===================
            // 按 UI 显示顺序组织，确保配置文件中的顺序与 UI 一致
            
            // --- 预测回复用的 stateless LLM 配置 ---
            _useOllama = Config.Bind("7. Predict LLM", "Use_Ollama_API", false, "预测回复使用 Ollama API");
            _thinkModeConfig = Config.Bind("7. Predict LLM", "ThinkMode", ThinkMode.Default, "预测回复的深度思考模式 (Default/Enable/Disable)");
            _chatApiUrlConfig = Config.Bind("7. Predict LLM", "API_URL",
                "http://127.0.0.1:12393/memory/chat",
                "预测回复使用的 stateless LLM API URL");
            _apiKeyConfig = Config.Bind("7. Predict LLM", "API_Key", "sk-or-v1-PasteYourKeyHere", "预测回复使用的 API Key");
            _modelConfig = Config.Bind("7. Predict LLM", "ModelName", "openai/gpt-3.5-turbo", "预测回复使用的模型名称");
            _logApiRequestBodyConfig = Config.Bind("7. Predict LLM", "LogApiRequestBody", false,
                "在日志中记录 API 请求体");
            _hideApiKeyInUiConfig = Config.Bind("7. Predict LLM", "HideApiKeyInUI", false,
                "隐藏预测回复 API Key 输入框（录屏用）");
            _fixApiPathForThinkModeConfig = Config.Bind("7. Predict LLM", "FixApiPathForThinkMode", true,
                "预测回复指定深度思考模式时尝试改用 Ollama 原生 API 路径");

            // --- TTS 配置 ---
            _sovitsUrlConfig = Config.Bind("2. TTS", "TTS_Service_URL", "http://127.0.0.1:9880", "兼容保留：主对话固定由 XnneHangLab Server 托管，此项仅保留旧配置兼容");
            _useGptSovitsTtsConfig = Config.Bind("2. TTS", "Use_GPT_SoVITS", true, "使用 GPT-SoVITS 作为 TTS 提供方");
            _useFasterQwenTtsConfig = Config.Bind("2. TTS", "Use_Faster_Qwen_TTS", false, "使用 faster-qwen-tts 作为 TTS 提供方");
            _refAudioPathConfig = Config.Bind("2. TTS", "Audio_File_Path", @"elaina.wav", "参考音频地址（gsv 默认 elaina.wav，qwen-tts 用绝对路径）");
            _audioPathCheckConfig = Config.Bind("2. TTS", "AudioPathCheck", false, "从 Mod 侧检测音频文件路径");
            _promptTextConfig = Config.Bind("2. TTS", "Audio_File_Text", "君が集中した時のシータ波を検出して、リンクをつなぎ直せば元通りになるはず。", "音频文件台词");
            _promptLangConfig = Config.Bind("2. TTS", "PromptLang", "ja", "音频文件语言 (prompt_lang)");
            _targetLangConfig = Config.Bind("2. TTS", "TargetLang", "ja", "合成语音语言 (text_lang)");
            _voiceVolumeConfig = Config.Bind("2. TTS", "VoiceVolume", 1.0f, "语音音量 (0.0 - 1.0)");
            NormalizeTtsProviderSelection();

            // --- 界面配置 ---
            // 我们希望窗口宽度是屏幕的 1/3，高度是屏幕的 1/3 (或者你喜欢的比例)
            float responsiveWidth = Screen.width * 0.3f; // 30% 屏幕宽度
            float responsiveHeight = Screen.height * 0.45f; // 45% 屏幕高度

            // 绑定配置 (默认值使用刚才算出来的动态值)
            _windowWidthConfig = Config.Bind("3. UI", "WindowWidth", responsiveWidth, "窗口宽度");
            _windowHeightConfig = Config.Bind("3. UI", "WindowHeightBase", responsiveHeight, "窗口高度");
            _reverseEnterBehaviorConfig = Config.Bind("3. UI", "ReverseEnterBehavior", false, 
                "反转回车键行为（勾选后：回车键换行、Shift+回车键发送；不勾选：回车键发送、Shift+回车键换行）");
            
            // 背景透明配置
            _backgroundOpacity = Config.Bind("3. UI", "BackgroundOpacity", 0.95f, "背景透明度 (0.0 - 1.0)");
            
            // 窗口标题显示配置
            _showWindowTitle = Config.Bind("3. UI", "ShowWindowTitle", true, "显示窗口标题");

            // --- 翻译配置 ---
            _enableTranslationConfig = Config.Bind("5. Translation", "EnableTranslation", false,
                "开启翻译（开启后请删掉系统提示词，无需利用提示词回复双语）");
            _deeplxUrlConfig = Config.Bind("5. Translation", "DeepLX_Url", "http://127.0.0.1:12393/translate/deeplx",
                "兼容保留：主对话固定由 XnneHangLab Server 托管，此项仅保留旧配置兼容");
            _translateSourceLangConfig = Config.Bind("5. Translation", "TranslateSourceLang", "ZH",
                "翻译源语言（如 ZH=中文，JA=日文，EN=英文）");
            _translateTargetLangConfig = Config.Bind("5. Translation", "TranslateTargetLang", "JA",
                "翻译目标语言（如 JA=日文，ZH=中文，EN=英文）");

            // --- 新增：XnneHangLab Chat Server 独立配置 ---
            _useXnneHangLabChatServer = Config.Bind("1. XnneHangLab Server", "Use_XnneHangLab_Chat_Server", true,
                "固定启用 XnneHangLab Server（客户端仅保留 Server 托管模式）");
            _xnneHangLabChatBaseUrl = Config.Bind("1. XnneHangLab Server", "Base_URL",
                "http://127.0.0.1:12393",
                "XnneHangLab Server 根地址（聊天、TTS、翻译端点都由此自动拼接）");
            _useXnneHangLabChatServer.Value = true;

            // --- 新增：预测对话配置 ---
            _predictAngelPromptConfig = Config.Bind("7. Predict", "Predict_Angel_Prompt",
                "You predict the user's next message. Output exactly one short, natural user-style sentence in a kind and supportive tone. No explanation, no JSON, no prefix.",
                "小天使预测 System Prompt");
            _predictDevilPromptConfig = Config.Bind("7. Predict", "Predict_Devil_Prompt",
                "You predict the user's next message. Output exactly one short, natural user-style sentence in a playful and teasing tone. No explanation, no JSON, no prefix.",
                "小恶魔预测 System Prompt");
            
            _enablePredictReply = Config.Bind("7. Predict", "Enable_Predict_Reply", false,
                "启用自动预测回复（AI 回复后自动生成预选回复，复用 LLM 配置）");

            // ===========================================

            // ================= 【修改点 2: 左上角对齐】 =================
            // 以前是 Screen.width / 2 (居中)，现在改为左上角 + 边距
            float margin = 20f; // 距离左上角的像素边距

            // 如果你是第一次运行（或者想强制重置位置），可以直接使用 margin
            // 但为了保留用户拖拽后的位置，通常不强制覆盖 _windowRect 的 x/y，
            // 除非你想每次启动都复位。这里我们演示【每次启动都复位到左上角】：
            
            _windowRect = new Rect(
                margin,               // X: 距离左边 20px
                margin,               // Y: 距离顶端 20px
                _windowWidthConfig.Value, 
                _windowHeightConfig.Value
            );

            // 初始化临时字符串
            _tempWidthString = _windowWidthConfig.Value.ToString("F0");
            _tempHeightString = _windowHeightConfig.Value.ToString("F0");
            _tempVolumeString = _voiceVolumeConfig.Value.ToString("F2");
            // 启动后台 TTS 健康检测
            if (_ttsHealthCheckCoroutine == null)
            {
                _ttsHealthCheckCoroutine = StartCoroutine(TTSHealthCheckLoop());
            }
            


            // 启动后台 DeepLX 健康检测
            if (_deeplxHealthCheckCoroutine == null)
            {
                _deeplxHealthCheckCoroutine = StartCoroutine(DeepLXHealthCheckLoop());
            }

            Log.Info($">>> AIMod V{AIChat.Version.VersionString}  已加载 <<<");
        }

        private bool _aiChatButtonAdded = false;
        private GameObject _aiChatButton;

        void Update()
        {
            // 自动连接游戏核心
            if (GameBridge._heroineService == null && Time.frameCount % 100 == 0) GameBridge.FindHeroineService();

            // 检查并添加AI聊天按钮
            if (!_aiChatButtonAdded && Time.frameCount % 300 == 0) // 每5秒检查一次，避免频繁查找
            {
                AddAIChatButtonToRightIcons();
            }
        }

        void OnGUI()
        {
            Event e = Event.current;
            if (e.isKey && e.type == EventType.KeyDown && (e.keyCode == KeyCode.F9 || e.keyCode == KeyCode.F10))
            {
                if (Time.unscaledTime - 0 > 0.2f) // 简单防抖
                {
                    _showInputWindow = !_showInputWindow;
                }
            }

            if (_showInputWindow)
            {
                // --- 1. 拖拽调整大小逻辑 ---
                if (_isResizing)
                {
                    Event currentEvent = Event.current;

                    if (currentEvent.type == EventType.MouseDrag)
                    {
                        // 鼠标位置 (currentEvent.mousePosition) 在 OnGUI 中是屏幕坐标
                        float newWidth = currentEvent.mousePosition.x - _windowRect.x;
                        float newHeight = currentEvent.mousePosition.y - _windowRect.y;

                        // 最小宽度和高度限制
                        _windowRect.width = Mathf.Max(300f, newWidth);
                        _windowRect.height = Mathf.Max(200f, newHeight);

                        currentEvent.Use();
                    }
                    else if (currentEvent.type == EventType.MouseUp)
                    {
                        _isResizing = false;

                        // 鼠标松开时，将新尺寸保存到配置项
                        _windowWidthConfig.Value = _windowRect.width;

                        // 计算新的基础高度 (即设置面板收起时的预期高度)
                        const float SettingsExtraHeight = 400f;
                        float newBaseHeight = _windowRect.height;

                        if (_showSettings)
                        {
                            newBaseHeight -= SettingsExtraHeight;
                        }

                        // 保存基础高度，并更新设置面板中的临时显示字符串
                        _windowHeightConfig.Value = Mathf.Max(100f, newBaseHeight);
                        _tempWidthString = _windowWidthConfig.Value.ToString("F0");
                        _tempHeightString = _windowHeightConfig.Value.ToString("F0");

                        currentEvent.Use();
                    }
                }
                else
                {
                    // --- 2. 如果没有拖拽，根据配置和设置状态计算窗口大小 (保持原逻辑) ---
                    _windowRect.width = _windowWidthConfig.Value;
                    float targetHeight = _windowHeightConfig.Value;

                    // 设置面板的额外高度
                    const float SettingsExtraHeight = 400f;
                    if (_showSettings)
                    {
                        targetHeight += SettingsExtraHeight;
                    }

                    _windowRect.height = Mathf.Max(targetHeight, 200f);
                }
                // --- 动态调整窗口高度和宽度结束 ---

                GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, _backgroundOpacity.Value);
                // 根据配置决定是否显示窗口标题
                string windowTitle = _showWindowTitle.Value ? "Chill AI 控制台" : "";
                _windowRect = GUI.Window(12345, _windowRect, DrawWindowContent, windowTitle);
                GUI.FocusWindow(12345);
            }
        }

        void DrawWindowContent(int windowID)
        {
            // ================= 【1. 动态尺寸计算】 =================
            // 根据屏幕高度计算基础字号 (2.5% 屏幕高度)
            int dynamicFontSize = (int)(Screen.height * 0.015f);
            dynamicFontSize = Mathf.Clamp(dynamicFontSize, 14, 40);

            // 全局样式应用
            GUI.skin.label.fontSize = dynamicFontSize;
            GUI.skin.button.fontSize = dynamicFontSize;
            GUI.skin.textField.fontSize = dynamicFontSize;
            GUI.skin.textArea.fontSize = dynamicFontSize;
            GUI.skin.toggle.fontSize = dynamicFontSize;
            GUI.skin.box.fontSize = dynamicFontSize;
            
            // 设置滚动条透明度跟随面板透明度
            // 创建自定义滚动条样式
            GUIStyle verticalScrollbarStyle = new GUIStyle(GUI.skin.verticalScrollbar);
            GUIStyle verticalScrollbarThumbStyle = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            GUIStyle horizontalScrollbarStyle = new GUIStyle(GUI.skin.horizontalScrollbar);
            GUIStyle horizontalScrollbarThumbStyle = new GUIStyle(GUI.skin.horizontalScrollbarThumb);
            
            // 创建半透明的纹理
            Texture2D scrollbarBgTexture = new Texture2D(1, 1);
            scrollbarBgTexture.SetPixel(0, 0, new Color(0.3f, 0.3f, 0.3f, _backgroundOpacity.Value));
            scrollbarBgTexture.Apply();
            
            Texture2D scrollbarThumbTexture = new Texture2D(1, 1);
            scrollbarThumbTexture.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, _backgroundOpacity.Value));
            scrollbarThumbTexture.Apply();
            
            // 设置滚动条样式
            verticalScrollbarStyle.normal.background = scrollbarBgTexture;
            verticalScrollbarStyle.hover.background = scrollbarBgTexture;
            verticalScrollbarStyle.active.background = scrollbarBgTexture;
            verticalScrollbarThumbStyle.normal.background = scrollbarThumbTexture;
            verticalScrollbarThumbStyle.hover.background = scrollbarThumbTexture;
            verticalScrollbarThumbStyle.active.background = scrollbarThumbTexture;
            
            horizontalScrollbarStyle.normal.background = scrollbarBgTexture;
            horizontalScrollbarStyle.hover.background = scrollbarBgTexture;
            horizontalScrollbarStyle.active.background = scrollbarBgTexture;
            horizontalScrollbarThumbStyle.normal.background = scrollbarThumbTexture;
            horizontalScrollbarThumbStyle.hover.background = scrollbarThumbTexture;
            horizontalScrollbarThumbStyle.active.background = scrollbarThumbTexture;
            
            // 应用自定义样式
            GUI.skin.verticalScrollbar = verticalScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = verticalScrollbarThumbStyle;
            GUI.skin.horizontalScrollbar = horizontalScrollbarStyle;
            GUI.skin.horizontalScrollbarThumb = horizontalScrollbarThumbStyle;

            // 基础行高
            float elementHeight = dynamicFontSize * 1.6f;

            // 常用宽度定义
            float labelWidth = elementHeight * 4f; 
            float inputWidth = elementHeight * 3f; 
            float btnWidth   = elementHeight * 2f; 
            // =======================================================

            // 开始滚动视图
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);
            
            // 开始整体垂直布局
            GUILayout.BeginVertical();

            // 版本信息显示
            GUILayout.Label($"版本：{AIChat.Version.VersionString}");

            // 状态显示
            string status = GameBridge._heroineService != null ? "🟢 核心已连接" : "🔴 正在寻找核心...";
            GUILayout.Label(status);

            string ttsStatus = _isTTSServiceReady ? "🟢 TTS 服务已就绪" : "🔴 正在等待 TTS 服务启动...";
            GUILayout.Label(ttsStatus);
            
            string translateStatus = _isDeepLXServiceReady ? "🟢 Translate 服务已连接" : "🔴 正在等待 Translate 服务启动...";
            GUILayout.Label(translateStatus);

            // 设置展开按钮 (全宽)
            string settingsBtnText = _showSettings ? "🔽 收起设置" : "▶️ 展开设置";
            if (GUILayout.Button(settingsBtnText, GUILayout.Height(elementHeight)))
            {
                _showSettings = !_showSettings;
            }

            // ================= 【设置面板区域】 =================
            if (_showSettings)
            {
                GUILayout.Space(10);

                // 【关键修复】统一计算内部 Box 宽度
                // 留出 50px 给滚动条和边框，防止爆边
                float innerBoxWidth = _windowRect.width - 50f; 

                // --- XnneHangLab Server 配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string xnneBtnText = _showXnneHangLabChatSettings ? "🔽 XnneHangLab Server 设置" : "▶️ XnneHangLab Server 设置";
                if (GUILayout.Button(xnneBtnText, GUILayout.Height(elementHeight)))
                {
                    _showXnneHangLabChatSettings = !_showXnneHangLabChatSettings;
                }

                if (_showXnneHangLabChatSettings)
                {
                    GUILayout.Space(5);
                    GUILayout.Label("Server 根地址：");
                    _xnneHangLabChatBaseUrl.Value = GUILayout.TextField(_xnneHangLabChatBaseUrl.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));

                    GUIStyle endpointStyle = new GUIStyle(GUI.skin.label);
                    endpointStyle.fontSize = Mathf.Max(10, GUI.skin.label.fontSize - 2);
                    Color prevServerColor = GUI.color;
                    GUI.color = new Color(0.7f, 0.9f, 1f);
                    GUILayout.Label($"  chat:      {GetChatUrl()}", endpointStyle);
                    GUILayout.Label($"  interrupt: {_xnneHangLabChatBaseUrl.Value.TrimEnd('/')}/memory/interrupt", endpointStyle);
                    GUILayout.Label($"  deeplx:    {GetDeepLXUrl()}", endpointStyle);
                    GUILayout.Label($"  gsv:       {TTSClient.GetGptSovitsEndpoint(GetTtsBaseUrl())}", endpointStyle);
                    GUILayout.Label($"  qwen-tts:  {TTSClient.GetQwenTtsStreamEndpoint(GetTtsBaseUrl())}", endpointStyle);
                    GUI.color = prevServerColor;

                    GUIStyle serverInfoStyle = new GUIStyle(GUI.skin.label);
                    serverInfoStyle.wordWrap = true;
                    GUILayout.Label("主对话、TTS、翻译固定走 XnneHangLab Server。", serverInfoStyle);
                    GUILayout.Label("长期记忆由后端托管，记忆数据存储在后端的 ./memory_bench 目录下。", serverInfoStyle);
                    GUILayout.Space(5);
                }

                GUILayout.EndVertical();

                GUILayout.Space(5);

                // --- 预测回复 LLM 配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string llmBtnText = _showLlmSettings ? "🔽 预测回复 LLM 配置" : "▶️ 预测回复 LLM 配置";
                if (GUILayout.Button(llmBtnText, GUILayout.Height(elementHeight)))
                {
                    _showLlmSettings = !_showLlmSettings;
                }

                if (_showLlmSettings)
                {
                    GUILayout.Space(5);
                    GUIStyle predictLlmInfoStyle = new GUIStyle(GUI.skin.label);
                    predictLlmInfoStyle.wordWrap = true;
                    GUILayout.Label("仅用于“预测回复”功能。主对话不会使用这里的 API URL / API Key / Model。", predictLlmInfoStyle);

                    bool newUseOllama = GUILayout.Toggle(_useOllama.Value, "预测回复使用 Ollama API", GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    _useOllama.Value = newUseOllama;

                    GUILayout.Space(5);
                    GUILayout.Label("API URL：");
                    _chatApiUrlConfig.Value = GUILayout.TextField(_chatApiUrlConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));

                    GUILayout.Space(5);
                    _hideApiKeyInUiConfig.Value = GUILayout.Toggle(
                        _hideApiKeyInUiConfig.Value,
                        "隐藏 API Key 输入框（录屏用）",
                        GUILayout.Height(elementHeight));

                    if (!_useOllama.Value && !_hideApiKeyInUiConfig.Value)
                    {
                        GUILayout.Label("API Key：");
                        _apiKeyConfig.Value = GUILayout.TextField(_apiKeyConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    }

                    if (!_useOllama.Value)
                    {
                        GUILayout.Label("模型名称：");
                        _modelConfig.Value = GUILayout.TextField(_modelConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    }

                    GUILayout.Space(5);
                    GUILayout.Label("指定深度思考（仅预测回复使用，当前主要给 Ollama）：");
                    string[] thinkModeOptions = { "不指定", "启用", "禁用" };
                    int currentMode = (int)_thinkModeConfig.Value;
                    int newMode = GUILayout.SelectionGrid(currentMode, thinkModeOptions, 3, GUILayout.Height(elementHeight));
                    if (newMode != currentMode)
                    {
                        _thinkModeConfig.Value = (ThinkMode)newMode;
                    }

                    GUILayout.Space(5);
                    _logApiRequestBodyConfig.Value = GUILayout.Toggle(_logApiRequestBodyConfig.Value, "在日志中记录 API 请求体", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                    _fixApiPathForThinkModeConfig.Value = GUILayout.Toggle(_fixApiPathForThinkModeConfig.Value, "指定深度思考模式时尝试改用 Ollama 原生 API 路径", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                }

                GUILayout.EndVertical();

                GUILayout.Space(5);

                // --- TTS 配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string ttsBtnText = _showTtsSettings ? "🔽 TTS 配置" : "▶️ TTS 配置";
                if (GUILayout.Button(ttsBtnText, GUILayout.Height(elementHeight)))
                {
                    _showTtsSettings = !_showTtsSettings;
                }

                if (_showTtsSettings)
                {
                    GUILayout.Space(5);

                    bool useGptSovits = GUILayout.Toggle(_useGptSovitsTtsConfig.Value, "使用 gpt-sovits", GUILayout.Height(elementHeight));
                    bool useFasterQwenTts = GUILayout.Toggle(_useFasterQwenTtsConfig.Value, "使用 faster-qwen-tts", GUILayout.Height(elementHeight));
                    ApplyTtsProviderToggleState(useGptSovits, useFasterQwenTts);

                    GUIStyle ttsEndpointStyle = new GUIStyle(GUI.skin.label);
                    ttsEndpointStyle.fontSize = Mathf.Max(10, GUI.skin.label.fontSize - 2);
                    Color prevTtsColor = GUI.color;
                    GUI.color = new Color(0.7f, 0.9f, 1f);
                    GUILayout.Label($"  gsv:   {TTSClient.GetGptSovitsEndpoint(GetTtsBaseUrl())}", ttsEndpointStyle);
                    GUILayout.Label($"  qwen:  {TTSClient.GetQwenTtsStreamEndpoint(GetTtsBaseUrl())}", ttsEndpointStyle);
                    GUI.color = prevTtsColor;
                    GUIStyle infoStyle = new GUIStyle(GUI.skin.label);
                    infoStyle.wordWrap = true;
                    GUILayout.Label("当前 TTS 已由 XnneHangLab Server 托管。", infoStyle);

                    GUILayout.Label("参考音频地址（gsv 默认 elaina.wav，qwen-tts 用绝对路径）：");
                    // 路径通常很长，必须加 MinWidth(50f)
                    _refAudioPathConfig.Value = GUILayout.TextField(_refAudioPathConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    GUILayout.Space(5);
                    _audioPathCheckConfig.Value = GUILayout.Toggle(_audioPathCheckConfig.Value, "从 Mod 侧检测音频文件路径", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                    
                    GUILayout.Label("音频文件台词：");
                    _promptTextConfig.Value = GUILayout.TextArea(_promptTextConfig.Value, GUILayout.Height(elementHeight * 3), GUILayout.MinWidth(50f));
                    
                    GUILayout.Space(5);
                    GUILayout.Label("音频文件语言 (prompt_lang):");
                    _promptLangConfig.Value = GUILayout.TextField(_promptLangConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    GUILayout.Label("合成语音语言 (text_lang):");
                    _targetLangConfig.Value = GUILayout.TextField(_targetLangConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));

                    GUILayout.Space(5);

                    GUILayout.Label($"语音音量：{_voiceVolumeConfig.Value:F2}");
                    
                    // 第一行：滑动条
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(5);
                    float newVolume = GUILayout.HorizontalSlider(_voiceVolumeConfig.Value, 0.0f, 1.0f);
                    GUILayout.Space(5);
                    GUILayout.EndHorizontal();

                    if (newVolume != _voiceVolumeConfig.Value)
                    {
                        _voiceVolumeConfig.Value = newVolume;
                        _audioSource.volume = newVolume;
                        _tempVolumeString = newVolume.ToString("F2");
                    }

                    // 第二行：输入框+按钮
                    GUILayout.Space(5);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("手动输入：", GUILayout.Width(labelWidth), GUILayout.Height(elementHeight));

                    _tempVolumeString = GUILayout.TextField(_tempVolumeString, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f)); 
                    if (GUILayout.Button("应用", GUILayout.Width(btnWidth), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempVolumeString, out float parsedVolume))
                        {
                            parsedVolume = Mathf.Clamp(parsedVolume, 0.0f, 1.0f);
                            _voiceVolumeConfig.Value = parsedVolume;
                            _audioSource.volume = parsedVolume;
                            _tempVolumeString = parsedVolume.ToString("F2");
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Space(10);

                }
                
                GUILayout.EndVertical();

                GUILayout.Space(5);

                // --- 界面配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string interfaceBtnText = _showInterfaceSettings ? "🔽 界面配置" : "▶️ 界面配置";
                if (GUILayout.Button(interfaceBtnText, GUILayout.Height(elementHeight)))
                {
                    _showInterfaceSettings = !_showInterfaceSettings;
                }
                if (_showInterfaceSettings)
                {
                    // 宽度设置
                    GUILayout.Label($"当前宽度：{_windowWidthConfig.Value:F0}px");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("新宽度：", GUILayout.Width(labelWidth), GUILayout.Height(elementHeight));
                    
                    // 【核心修改】允许缩小
                    _tempWidthString = GUILayout.TextField(_tempWidthString, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    if (GUILayout.Button("应用", GUILayout.Width(btnWidth), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempWidthString, out float newWidth) && newWidth >= 300f)
                        {
                            _windowWidthConfig.Value = newWidth;
                            // 这里删除了重置居中代码，只改大小
                            _tempWidthString = newWidth.ToString("F0");
                        }
                    }
                    GUILayout.EndHorizontal();

                    // 高度设置
                    GUILayout.Label($"当前基础高度: {_windowHeightConfig.Value:F0}px");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("新高度:", GUILayout.Width(labelWidth), GUILayout.Height(elementHeight));
                    
                    // 【核心修改】允许缩小
                    _tempHeightString = GUILayout.TextField(_tempHeightString, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    if (GUILayout.Button("应用", GUILayout.Width(btnWidth), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempHeightString, out float newHeight) && newHeight >= 100f)
                        {
                            _windowHeightConfig.Value = newHeight;
                            _tempHeightString = newHeight.ToString("F0");
                        }
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(5);
                    
                    // 窗口标题显示配置
                    _showWindowTitle.Value = GUILayout.Toggle(_showWindowTitle.Value, 
                        "显示窗口标题", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                    
                    // 背景透明配置
                    GUILayout.Label($"背景透明度：{_backgroundOpacity.Value:F2}");
                    
                    // 滑动条
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(5);
                    float newOpacity = GUILayout.HorizontalSlider(_backgroundOpacity.Value, 0.0f, 1.0f);
                    GUILayout.Space(5);
                    GUILayout.EndHorizontal();
                    
                    if (newOpacity != _backgroundOpacity.Value)
                    {
                        _backgroundOpacity.Value = newOpacity;
                    }
                    
                    GUILayout.Space(5);

                    // 快捷键配置
                    _reverseEnterBehaviorConfig.Value = GUILayout.Toggle(_reverseEnterBehaviorConfig.Value, 
                        "反转回车键行为（勾选后：回车换行，Shift+回车发送）", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                }
                
                GUILayout.EndVertical(); 
                GUILayout.Space(5);

                // ================= 预测对话配置区域 =================
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string predictBtnText = _showPredictSettings ? "🔽 预测对话设置" : "▶️ 预测对话设置";
                if (GUILayout.Button(predictBtnText, GUILayout.Height(elementHeight)))
                {
                    _showPredictSettings = !_showPredictSettings;
                }
                
                if (_showPredictSettings)
                {
                    GUILayout.Space(5);
                    
                    // 启用开关
                    _enablePredictReply.Value = GUILayout.Toggle(
                        _enablePredictReply.Value,
                        "启用自动预测回复（AI 回复后自动生成预选回复）",
                        GUILayout.Height(elementHeight));
                    
                    GUILayout.Space(5);
                    
                    if (_enablePredictReply.Value)
                    {
                        // 提示信息
                        GUIStyle infoStyle2 = new GUIStyle(GUI.skin.label);
                        infoStyle2.wordWrap = true;
                        Color prevC2 = GUI.color;
                        GUI.color = new Color(0.7f, 0.9f, 1f);
                        GUILayout.Label("ℹ️ 预测功能复用“预测回复 LLM 配置”（stateless）", infoStyle2);
                        GUILayout.Label("ℹ️ 触发时机：AI 完整回复后自动预测，TTS 播放完毕后显示预览", infoStyle2);
                        GUI.color = prevC2;
                    }
                    
                    GUILayout.Space(5);
                    GUILayout.Label("小天使预测 System Prompt：");
                    _predictAngelPromptScrollPosition = GUILayout.BeginScrollView(
                        _predictAngelPromptScrollPosition,
                        GUILayout.Height(elementHeight * 4));
                    _predictAngelPromptConfig.Value = GUILayout.TextArea(
                        _predictAngelPromptConfig.Value,
                        GUILayout.ExpandHeight(true));
                    GUILayout.EndScrollView();

                    GUILayout.Space(5);
                    GUILayout.Label("小恶魔预测 System Prompt：");
                    _predictDevilPromptScrollPosition = GUILayout.BeginScrollView(
                        _predictDevilPromptScrollPosition,
                        GUILayout.Height(elementHeight * 4));
                    _predictDevilPromptConfig.Value = GUILayout.TextArea(
                        _predictDevilPromptConfig.Value,
                        GUILayout.ExpandHeight(true));
                    GUILayout.EndScrollView();
                    
                    GUILayout.Space(5);
                }
                
                GUILayout.EndVertical();

                GUILayout.Space(10);

                // ================= 翻译配置区域 =================
                GUILayout.BeginVertical("box");
                string translationBtnText = _showTranslationSettings ? "🔽 翻译配置" : "▶️ 翻译配置";
                if (GUILayout.Button(translationBtnText, GUILayout.Height(elementHeight)))
                {
                    _showTranslationSettings = !_showTranslationSettings;
                }

                if (_showTranslationSettings)
                {
                    GUILayout.Space(5);

                    _enableTranslationConfig.Value = GUILayout.Toggle(
                        _enableTranslationConfig.Value, "启用翻译", GUILayout.Height(elementHeight));

                    GUILayout.Space(5);

                    GUIStyle warnStyle = new GUIStyle(GUI.skin.label);
                    warnStyle.wordWrap = true;
                    Color prevColor = GUI.color;
                    GUI.color = new Color(1f, 0.8f, 0.2f);
                    GUILayout.Label("⚠️ 开启翻译后请删掉系统提示词，无需利用提示词回复双语", warnStyle, GUILayout.Height(elementHeight * 2));
                    GUI.color = prevColor;

                    GUILayout.Space(5);

                    GUIStyle infoStyle = new GUIStyle(GUI.skin.label);
                    infoStyle.wordWrap = true;
                    Color prevC2 = GUI.color;
                    GUI.color = new Color(0.7f, 0.9f, 1f);
                    GUILayout.Label($"DeepLX 已由 XnneHangLab Server 托管：{GetDeepLXUrl()}", infoStyle);
                    GUI.color = prevC2;

                    GUILayout.Space(5);

                    GUILayout.Label("翻译源语言:");
                    _translateSourceLangConfig.Value = GUILayout.TextField(_translateSourceLangConfig.Value, GUILayout.Height(elementHeight));
                    GUILayout.Label("示例：ZH=中文，JA=日文，EN=英文", GUILayout.Height(elementHeight));

                    GUILayout.Space(5);

                    GUILayout.Label("翻译目标语言:");
                    _translateTargetLangConfig.Value = GUILayout.TextField(_translateTargetLangConfig.Value, GUILayout.Height(elementHeight));
                    GUILayout.Label("示例：JA=日文，ZH=中文，EN=英文", GUILayout.Height(elementHeight));

                    GUILayout.Space(5);
                }
                GUILayout.EndVertical();

                GUILayout.Space(10);

                // 保存按钮
                if (GUILayout.Button("💾 保存所有配置", GUILayout.Height(elementHeight * 1.5f)))
                {
                    Config.Save();
                    Log.Info("配置已保存！");
                }
                GUILayout.Space(10);
            }
            // ================= 设置面板结束 =================

            // === 对话区域 ===
            GUILayout.Space(10);
            GUILayout.Label("<b>与聪音对话：</b>");

            GUI.backgroundColor = Color.white;

            // 动态计算输入框高度
            float dynamicInputHeight = _windowRect.height - (elementHeight * 3.5f);
            dynamicInputHeight = Mathf.Clamp(dynamicInputHeight, 50f, Screen.height * 0.8f);

            GUIStyle largeInputStyle = new GUIStyle(GUI.skin.textArea);
            largeInputStyle.fontSize = (int)(dynamicFontSize * 1.4f);
            largeInputStyle.wordWrap = true;
            largeInputStyle.alignment = TextAnchor.UpperLeft;

            GUI.skin.textArea.wordWrap = true;
            
            // 处理快捷键（回车和 Shift+回车）- 必须在 TextArea 之前处理
            Event keyEvent = Event.current;
            bool shouldSendMessage = false;
            
            if (keyEvent.type == EventType.KeyDown && 
                keyEvent.keyCode == KeyCode.Return && 
                !_isProcessing &&
                !string.IsNullOrEmpty(_playerInput))
            {
                // 检测是否按下 Shift 键
                bool shiftPressed = keyEvent.shift;
                
                // 根据配置决定是否应该发送
                // 默认模式（_reverseEnterBehaviorConfig = false）：Enter 发送，Shift+Enter 换行
                // 反转模式（_reverseEnterBehaviorConfig = true）：Enter 换行，Shift+Enter 发送
                shouldSendMessage = _reverseEnterBehaviorConfig.Value ? shiftPressed : !shiftPressed;
            }
            
            // 如果需要发送消息，在渲染 TextArea 之前拦截事件
            if (shouldSendMessage)
            {
                InterruptCurrentProcess(false);
                _isInterrupted = false;
                _aiProcessCoroutine = StartCoroutine(AIProcessRoutine(_playerInput));
                _playerInput = "";
                keyEvent.Use(); // 消费事件，防止 TextArea 处理
            }
            
            _playerInput = GUILayout.TextArea(_playerInput, largeInputStyle, GUILayout.Height(dynamicInputHeight));

            GUILayout.Space(5);
            
            // === 预测回复显示区域 ===
            if (_isPredicting)
            {
                // 显示加载状态
                float predictBoxWidth = _windowRect.width - 50f;
                GUILayout.BeginVertical("box", GUILayout.Width(predictBoxWidth));
                GUILayout.Label("💡 正在生成预测回复...", GUILayout.Height(elementHeight));
                GUILayout.EndVertical();
                GUILayout.Space(5);
            }
            else if (_showPredictedReplies && (!string.IsNullOrEmpty(_predictedAngelReply) || !string.IsNullOrEmpty(_predictedDevilReply)))
            {
                float predictBoxWidth = _windowRect.width - 50f; // 与设置框对齐
                GUILayout.BeginVertical("box", GUILayout.Width(predictBoxWidth));
                
                GUIStyle predictTitleStyle = new GUIStyle(GUI.skin.label);
                predictTitleStyle.fontStyle = FontStyle.Bold;
                GUILayout.Label("💡 预测回复（点击填充）", predictTitleStyle, GUILayout.Height(elementHeight));
                
                GUILayout.Space(3);
                
                // 小天使选项（不显示扎眼前缀）
                if (!string.IsNullOrEmpty(_predictedAngelReply))
                {
                    if (GUILayout.Button(_predictedAngelReply, GUILayout.Height(elementHeight * 2)))
                    {
                        _playerInput = _predictedAngelReply;
                        _showPredictedReplies = false;
                    }
                    GUILayout.Space(3);
                }
                
                // 小恶魔选项（不显示扎眼前缀）
                if (!string.IsNullOrEmpty(_predictedDevilReply))
                {
                    if (GUILayout.Button(_predictedDevilReply, GUILayout.Height(elementHeight * 2)))
                    {
                        _playerInput = _predictedDevilReply;
                        _showPredictedReplies = false;
                    }
                }
                
                GUILayout.EndVertical();
                GUILayout.Space(5);
            }
            
            GUI.backgroundColor = _isProcessing ? Color.gray : new Color(0.1725f, 0.1608f, 0.2784f);

            GUILayout.BeginHorizontal();

            // 1. 计算精确宽度
            // _windowRect.width - 50f 是我们之前定义的 innerBoxWidth (与设置框对齐)
            // 再减去 4f 是为了留出两个按钮中间的缝隙
            float totalWidth = _windowRect.width - 50f;
            float singleBtnWidth = (totalWidth - 4f) / 2f;

            // ================== 发送 / 中断按钮 ==================
            // 使用 GUILayout.Width(singleBtnWidth) 强制固定宽度
            if (_isProcessing)
            {
                // 处理中：显示中断按钮
                GUI.backgroundColor = new Color(0.85f, 0.2f, 0.2f); // 红色
                if (GUILayout.Button("⛔ 中断", GUILayout.Height(elementHeight * 1.5f), GUILayout.Width(singleBtnWidth)))
                {
                    InterruptCurrentProcess(true);
                }
                GUI.backgroundColor = Color.white;
            }
            else
            {
                if (GUILayout.Button("发送", GUILayout.Height(elementHeight * 1.5f), GUILayout.Width(singleBtnWidth)))
                {
                    if (!string.IsNullOrEmpty(_playerInput))
                    {
                        InterruptCurrentProcess(false);
                        _isInterrupted = false;
                        _aiProcessCoroutine = StartCoroutine(AIProcessRoutine(_playerInput));
                        _playerInput = "";
                    }
                }
            }

            // ================== 录音按钮 ==================
            if (_isProcessing)
            {
                GUI.backgroundColor = Color.gray; 
            }
            else
            {
                GUI.backgroundColor = _isRecording ? Color.red : new Color(0.1725f, 0.1608f, 0.2784f);
            }
            string micBtnText;
            if (_isProcessing)
            {
                micBtnText = "⏳ 思考中...";
            }
            else
            {
                micBtnText = _isRecording ? "🔴 松开结束" : "🎤 按住说话";
            }

            // 使用 GUILayout.Width(singleBtnWidth) 强制固定宽度
            Rect btnRect = GUILayoutUtility.GetRect(
                new GUIContent(micBtnText), 
                GUI.skin.button, 
                GUILayout.Height(elementHeight * 1.5f), 
                GUILayout.Width(singleBtnWidth) // <--- 强制宽度，不再依赖自动扩展
            );

            Event e = Event.current;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (btnRect.Contains(e.mousePosition) && !_isProcessing)
                    {
                        GUIUtility.hotControl = controlID; 
                        StartRecording();
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID)
                    {
                        GUIUtility.hotControl = 0;
                        StopRecordingAndRecognize();
                        e.Use();
                    }
                    break;
            }

            GUI.Box(btnRect, micBtnText, GUI.skin.button);

            GUILayout.EndHorizontal();

            // 结束整体布局
            GUILayout.EndVertical();
            GUILayout.EndScrollView();

            // --- 拖拽手柄 ---
            const float handleSize = 25f;
            Rect handleRect = new Rect(_windowRect.width - handleSize, _windowRect.height - handleSize, handleSize, handleSize);
            GUI.Box(handleRect, "⇲", GUI.skin.GetStyle("Button"));

            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && handleRect.Contains(currentEvent.mousePosition))
            {
                if (currentEvent.button == 0)
                {
                    _isResizing = true;
                    currentEvent.Use();
                }
            }

            if (!_isResizing)
            {
                GUI.DragWindow();
            }
        }

        private void BringOverlayToFront(Text text)
        {
            if (text == null || text.transform == null) return;
            text.transform.SetAsLastSibling();
        }

        IEnumerator AIProcessRoutine(string prompt)
        {
            _isProcessing = true;

            // 1. 获取并处理 UI
            GameObject canvas = GameObject.Find("Canvas");
            if (canvas == null) { _isProcessing = false; yield break; }
            Transform originalTextTrans = canvas.transform.Find("StorySystemUI/MessageWindow/NormalTextParent/NormalTextMessage");
            if (originalTextTrans == null) { _isProcessing = false; yield break; }
            GameObject originalTextObj = originalTextTrans.gameObject;
            GameObject parentObj = originalTextObj.transform.parent.gameObject;
            Dictionary<GameObject, bool> uiStatusMap = new Dictionary<GameObject, bool>();
            UIHelper.ForceShowWindow(originalTextObj, uiStatusMap);
            originalTextObj.SetActive(false);
            GameObject myTextObj = UIHelper.CreateOverlayText(parentObj);
            Text myText = myTextObj.GetComponent<Text>();
            TrackActiveUiState(uiStatusMap, myTextObj, originalTextObj);
            BringOverlayToFront(myText);
            myText.text = "Thinking..."; myText.color = Color.yellow;

            // 2. 准备请求数据
            // 主对话固定走 XnneHangLab Server，不再发送客户端侧 SystemPrompt。
            var requestContext = new LLMRequestContext
            {
                ApiUrl = GetChatUrl(),
                ApiKey = "",
                ModelName = "",
                SystemPrompt = "",
                UserPrompt = prompt,
                UseLocalOllama = false,
                UseXnneHangLab = false,
                UseXnneHangLabChatServer = true,
                LogApiRequestBody = _logApiRequestBodyConfig.Value,
                ThinkMode = ThinkMode.Default,
                HierarchicalMemory = null,
                LogHeader = "AIChat",
                FixApiPathForThinkMode = false,
                EnableTranslation = _enableTranslationConfig.Value,
                DeepLXUrl = GetDeepLXUrl(),
                TranslateTargetLang = _translateTargetLangConfig.Value
            };

            string fullResponse = "";
            string errMsg = "";
            long errCode = 0;

            bool success = false;

            // 3. 发送 Chat 请求
            yield return LLMClient.SendLLMRequest(
                requestContext,
                rawResponse =>
                {
                    // XnneHangLab /memory/chat 端点返回纯文本，不需要特殊解析
                    // 暫時用 ExtractContentRegex 解析 content 字段，保持邏輯一致性
                    // TODO: 以後改用 JsonUtility 直接解析
                    if (requestContext.UseXnneHangLabChatServer)
                    {
                        fullResponse = ResponseParser.ExtractContentRegex(rawResponse);
                    }
                    else if (requestContext.UseLocalOllama)
                    {
                        fullResponse = ResponseParser.ExtractContentFromOllama(rawResponse);
                    }
                    else
                    {
                        fullResponse = ResponseParser.ExtractContentRegex(rawResponse);
                    }
                    success = true;
                },
                (errorMsg, responseCode) =>
                {
                    errCode = responseCode;
                    errMsg = $"API Error: {errorMsg}\nCode: {responseCode}";
                    success = false;
                }
            );

            if (!success)
            {
                // 报错时的处理逻辑
                if (errCode == 401) errMsg += "\n(请检查 API Key 是否正确)";
                if (errCode == 404) errMsg += "\n(模型名称或 URL 错误)";

                myText.text = errMsg;
                BringOverlayToFront(myText);
                myText.color = Color.red;

                // 让错误信息在屏幕上停留 3 秒，让玩家看清楚
                yield return new WaitForSecondsRealtime(3.0f);

                // 手动执行清理工作，恢复游戏原本状态
                CleanupActiveUiState();
                _isInterrupted = false;
                _isProcessing = false;
                _aiProcessCoroutine = null;
                yield break;
            }

            // 4. 处理回复并下载语音
            if (!string.IsNullOrEmpty(fullResponse))
            {
                LLMStandardResponse parsedResponse = LLMUtils.ParseStandardResponse(fullResponse);
                string emotionTag = parsedResponse.EmotionTag;
                // 解析后立即剥离 [Emotion] ||| 前缀，防止解析异常时前缀混入 TTS/字幕
                string voiceText = ResponseParser.StripEmotionPrefix(parsedResponse.VoiceText);
                string subtitleText = ResponseParser.StripEmotionPrefix(parsedResponse.SubtitleText);

                // 仅在 voiceText 非空时请求 TTS，不再按日文内容做拦截
                if (!string.IsNullOrEmpty(voiceText))
                {
                    myText.text = "message is sending through cyber space";
                    BringOverlayToFront(myText);
                    
                    // 【关键判断】仅在 TTS 服务就绪时启用流式断句播放
                    if (_isTTSServiceReady)
                    {
                        // 流式 TTS 模式：断句 + 异步生成 + 逐句播放
                        Log.Info("[TTS] 服务就绪，启用流式断句播放");
                        yield return StartCoroutine(PlayStreamingTTS(
                            fullResponse,
                            voiceText,
                            subtitleText,
                            emotionTag,
                            GetTtsBaseUrl(),
                            _targetLangConfig.Value,
                            _refAudioPathConfig.Value,
                            _promptTextConfig.Value,
                            _promptLangConfig.Value,
                            _audioPathCheckConfig.Value,
                            myText,
                            _enableTranslationConfig.Value,
                            GetDeepLXUrl(),
                            _translateSourceLangConfig.Value,
                            _translateTargetLangConfig.Value
                        ));
                    }
                    else
                    {
                        // 传统模式：TTS 未就绪，完整文本一次性生成
                        Log.Info("[TTS] 服务未就绪，使用传统完整文本模式");
                        AudioClip downloadedClip = null;
                        if (GetCurrentTtsProvider() == TTSClient.Provider.FasterQwenTts)
                        {
                            yield return StartCoroutine(TTSClient.DownloadQwenVoiceWithRetry(
                                GetTtsBaseUrl(),
                                voiceText,
                                _refAudioPathConfig.Value,
                                _promptTextConfig.Value,
                                Logger,
                                (clip) => downloadedClip = clip,
                                3,
                                30f,
                                _audioPathCheckConfig.Value));
                        }
                        else
                        {
                            yield return StartCoroutine(TTSClient.DownloadVoiceWithRetry(
                                TTSClient.GetGptSovitsEndpoint(GetTtsBaseUrl()),
                                voiceText,
                                _targetLangConfig.Value,
                                _refAudioPathConfig.Value,
                                _promptTextConfig.Value,
                                _promptLangConfig.Value,
                                Logger,
                                (clip) => downloadedClip = clip,
                                3,
                                30f,
                                _audioPathCheckConfig.Value));
                        }

                        if (downloadedClip != null)
                        {
                            if (!downloadedClip.LoadAudioData()) yield return null;
                            yield return null;

                            // 【应用换行】在将字幕文本显示到 UI 之前，强制插入换行符
                            subtitleText = ResponseParser.InsertLineBreaks(subtitleText, 25);
                            myText.text = subtitleText;
                            BringOverlayToFront(myText);
                            myText.color = Color.white;

                            // 正常播放
                            yield return StartCoroutine(PlayNativeAnimation(emotionTag, downloadedClip));
                        }
                        else
                        {
                            myText.text = "Voice Failed (TTS Error)";
                            BringOverlayToFront(myText);
                            // 语音失败时，至少做个动作显示字幕
                            subtitleText = ResponseParser.InsertLineBreaks(subtitleText, 25);
                            myText.text = subtitleText;
                            BringOverlayToFront(myText);
                            yield return StartCoroutine(PlayNativeAnimation(emotionTag, null)); // 传 null 进去
                        }
                    }
                }
                else
                {
                    // 【静音模式】
                    // 如果格式错了或文本为空，我们就只显示字幕、做动作，不发声音
                    Log.Warning("跳过 TTS：文本为空");

                    myText.text = subtitleText;
                    BringOverlayToFront(myText);
                    myText.color = Color.white;

                    // 修改 PlayNativeAnimation 支持无音频模式 (见下方)
                    yield return StartCoroutine(PlayNativeAnimation(emotionTag, null));
                }
            }

            // 5. 更新预测上下文
            UpdatePredictContext(prompt, fullResponse);
            
            // 6. 触发预测回复（如果启用且未中断）
            if (_enablePredictReply.Value && !_isInterrupted && !string.IsNullOrEmpty(fullResponse))
            {
                // 异步触发预测，不阻塞 UI
                StartCoroutine(TriggerPredictReply(fullResponse));
            }
            
            // 6. 清理
            CleanupActiveUiState();
            _isInterrupted = false;
            _isProcessing = false;
            _aiProcessCoroutine = null;
        }


        /// <summary>
        /// 流式 TTS 播放：断句 + DeepLX 翻译 + 异步生成队列 + 逐句同步播放
        /// 仅在 TTS 服务就绪时调用
        /// 工作流：中文字幕分句 → 字幕直接入队显示 → DeepLX(ZH→JA) → 日文送 TTS
        /// </summary>
        List<StreamingSentenceTask> BuildStreamingSentenceTasks(
            string rawResponse,
            string fallbackVoiceText,
            string fallbackSubtitleText,
            string defaultEmotionTag)
        {
            var tasks = new List<StreamingSentenceTask>();
            string safeDefaultEmotion = string.IsNullOrWhiteSpace(defaultEmotionTag) ? "Think" : defaultEmotionTag.Trim();

            // 优先按 [Emotion] ||| 分块，支持一次回复多个动作段。
            if (!string.IsNullOrWhiteSpace(rawResponse))
            {
                var tagMatches = System.Text.RegularExpressions.Regex.Matches(
                    rawResponse,
                    @"\[(?<emotion>[^\]\r\n]+)\]\s*\|\|\|\s*",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                if (tagMatches.Count > 0)
                {
                    for (int i = 0; i < tagMatches.Count; i++)
                    {
                        string emotion = tagMatches[i].Groups["emotion"].Value.Trim();
                        if (string.IsNullOrWhiteSpace(emotion))
                            emotion = safeDefaultEmotion;

                        int start = tagMatches[i].Index + tagMatches[i].Length;
                        int end = (i + 1 < tagMatches.Count) ? tagMatches[i + 1].Index : rawResponse.Length;
                        int len = end - start;
                        if (len <= 0) continue;

                        string chunkText = rawResponse.Substring(start, len).Trim();
                        chunkText = ResponseParser.StripEmotionPrefix(chunkText);
                        if (string.IsNullOrWhiteSpace(chunkText)) continue;

                        string[] splitSentences = ResponseParser.SplitByChinesePunctuation(chunkText);
                        if (splitSentences.Length == 0)
                        {
                            tasks.Add(new StreamingSentenceTask(emotion, chunkText));
                        }
                        else
                        {
                            foreach (string sentence in splitSentences)
                            {
                                if (!string.IsNullOrWhiteSpace(sentence))
                                    tasks.Add(new StreamingSentenceTask(emotion, sentence.Trim()));
                            }
                        }
                    }
                }
            }

            if (tasks.Count > 0)
                return tasks;

            // 回退：沿用原逻辑（按 subtitle 断句，全部使用默认动作）。
            fallbackSubtitleText = ResponseParser.StripEmotionPrefix(fallbackSubtitleText);
            fallbackVoiceText = ResponseParser.StripEmotionPrefix(fallbackVoiceText);
            string[] fallbackSentences = ResponseParser.SplitByChinesePunctuation(fallbackSubtitleText);
            if (fallbackSentences.Length == 0 && !string.IsNullOrWhiteSpace(fallbackVoiceText))
                fallbackSentences = new string[] { fallbackVoiceText };

            foreach (string sentence in fallbackSentences)
            {
                if (!string.IsNullOrWhiteSpace(sentence))
                    tasks.Add(new StreamingSentenceTask(safeDefaultEmotion, sentence.Trim()));
            }

            return tasks;
        }

        IEnumerator PlayStreamingTTS(
            string rawResponse,
            string fullVoiceText,
            string fullSubtitleText,
            string emotionTag,
            string ttsBaseUrl,
            string targetLang,
            string refAudioPath,
            string promptText,
            string promptLang,
            bool audioPathCheck,
            UnityEngine.UI.Text myText,
            bool enableTranslation,
            string deeplxUrl,
            string sourceLang,
            string translateTargetLang)
        {
            // 0. 构建逐句任务（每句包含 emotion + 字幕文本）
            List<StreamingSentenceTask> sentences = BuildStreamingSentenceTasks(
                rawResponse,
                fullVoiceText,
                fullSubtitleText,
                emotionTag);
            if (sentences.Count == 0)
            {
                Log.Warning("[流式 TTS] 无可播放句子，提前结束");
                yield break;
            }

            Log.Info($"[流式 TTS] 断句完成，共 {sentences.Count} 句");

            if (GetCurrentTtsProvider() == TTSClient.Provider.FasterQwenTts)
            {
                yield return StartCoroutine(PlayStreamingQwenTTS(
                    sentences,
                    ttsBaseUrl,
                    refAudioPath,
                    promptText,
                    audioPathCheck,
                    myText,
                    enableTranslation,
                    deeplxUrl,
                    sourceLang,
                    translateTargetLang));
                yield break;
            }
            
            // 2. 队列管理
            Queue<AudioClip> audioQueue = new Queue<AudioClip>();
            Queue<string> subtitleQueue = new Queue<string>();
            Queue<string> emotionQueue = new Queue<string>();
            
            // 3. 启动后台 TTS+ 翻译生成协程
            StartCoroutine(TTSWithTranslationGeneratorLoop(
                sentences,
                ttsBaseUrl,
                targetLang,
                refAudioPath,
                promptText,
                promptLang,
                audioPathCheck,
                audioQueue,
                subtitleQueue,
                emotionQueue,
                enableTranslation,
                deeplxUrl,
                sourceLang,
                translateTargetLang
            ));
            
            // 4. 播放循环
            string lastPlayedEmotion = null;
            for (int sentenceIndex = 0; sentenceIndex < sentences.Count; sentenceIndex++)
            {
                // 中断检查
                if (_isInterrupted)
                {
                    Log.Info("[流式 TTS] 收到中断信号，停止播放");
                    audioQueue.Clear();
                    subtitleQueue.Clear();
                    emotionQueue.Clear();
                    break;
                }

                // 等待队列里有音频（等待期间也检查中断）
                while (audioQueue.Count == 0 || subtitleQueue.Count == 0 || emotionQueue.Count == 0)
                {
                    if (_isInterrupted)
                    {
                        Log.Info("[流式 TTS] 等待期间收到中断信号，停止播放");
                        audioQueue.Clear();
                        subtitleQueue.Clear();
                        emotionQueue.Clear();
                        yield break;
                    }
                    myText.text = $"正在生成语音... ({sentenceIndex + 1}/{sentences.Count})";
                    BringOverlayToFront(myText);
                    yield return null;
                }
                
                AudioClip clip = audioQueue.Dequeue();
                string subtitle = subtitleQueue.Count > 0 ? subtitleQueue.Dequeue() : ResponseParser.InsertLineBreaks(fullSubtitleText, 25);
                string sentenceEmotion = emotionQueue.Count > 0 ? emotionQueue.Dequeue() : emotionTag;
                if (string.IsNullOrWhiteSpace(sentenceEmotion)) sentenceEmotion = "Think";
                bool shouldSwitchEmotion = string.IsNullOrEmpty(lastPlayedEmotion)
                    || !string.Equals(lastPlayedEmotion, sentenceEmotion, StringComparison.OrdinalIgnoreCase);
                
                if (clip != null)
                {
                    if (!clip.LoadAudioData()) yield return null;
                    yield return null;
                    
                    // 显示字幕（逐句）
                    myText.text = subtitle;
                    BringOverlayToFront(myText);
                    myText.color = Color.white;
                    
                    if (shouldSwitchEmotion)
                    {
                        // emotion 变化时才切动作，避免同动作重复触发导致抽搐。
                        yield return StartCoroutine(PlayNativeAnimation(sentenceEmotion, clip));
                    }
                    else
                    {
                        // emotion 不变：只播音频，不重复切动作。
                        yield return StartCoroutine(PlayAudioWithoutEmotionSwitch(clip));
                    }
                }
                else
                {
                    Log.Warning($"[流式 TTS] 第 {sentenceIndex + 1} 句生成失败，跳过");
                    myText.text = subtitle;
                    BringOverlayToFront(myText);
                    myText.color = Color.white;
                    if (shouldSwitchEmotion)
                        yield return StartCoroutine(PlayNativeAnimation(sentenceEmotion, null));
                    else
                        yield return new WaitForSecondsRealtime(0.1f);
                }
                lastPlayedEmotion = sentenceEmotion;
            }
            
            HandlePendingPredictResultAfterTTS();
            Log.Info(_isInterrupted ? "[流式 TTS] 已中断" : "[流式 TTS] 播放完成");
        }

        IEnumerator PlayStreamingQwenTTS(
            List<StreamingSentenceTask> sentences,
            string ttsBaseUrl,
            string refAudioPath,
            string promptText,
            bool audioPathCheck,
            UnityEngine.UI.Text myText,
            bool enableTranslation,
            string deeplxUrl,
            string sourceLang,
            string translateTargetLang)
        {
            CancelAllQwenStreams();

            var sessions = new List<QwenStreamingSentenceSession>();
            var preparedSentences = new List<QwenPreparedSentence>(sentences.Count);
            for (int i = 0; i < sentences.Count; i++)
                preparedSentences.Add(new QwenPreparedSentence { Index = i });
            bool generationFinished = false;
            bool translationFinished = false;

            StartCoroutine(QwenTranslationPrefetchLoop(
                sentences,
                enableTranslation,
                deeplxUrl,
                sourceLang,
                translateTargetLang,
                preparedSentences,
                () => translationFinished = true));

            StartCoroutine(QwenPrefetchGeneratorLoop(
                preparedSentences,
                ttsBaseUrl,
                refAudioPath,
                promptText,
                audioPathCheck,
                sessions,
                () => translationFinished,
                () => generationFinished = true));

            string lastPlayedEmotion = null;
            for (int i = 0; i < sentences.Count; i++)
            {
                if (_isInterrupted)
                    break;

                while (sessions.Count <= i && !generationFinished)
                {
                    if (_isInterrupted)
                        yield break;
                    if (myText != null)
                    {
                        myText.text = $"正在准备语音... ({i + 1}/{sentences.Count})";
                        BringOverlayToFront(myText);
                    }
                    yield return null;
                }

                if (sessions.Count <= i)
                    break;

                QwenStreamingSentenceSession session = sessions[i];
                bool shouldSwitchEmotion = string.IsNullOrEmpty(lastPlayedEmotion)
                    || !string.Equals(lastPlayedEmotion, session.EmotionTag, StringComparison.OrdinalIgnoreCase);

                yield return StartCoroutine(PlayQwenSessionWhenReady(session, shouldSwitchEmotion, myText));
                lastPlayedEmotion = session.EmotionTag;

                if (session.StreamTask != null)
                {
                    while (!session.StreamTask.IsCompleted)
                        yield return null;
                }

                UnregisterQwenStreamCancellation(session.Cancellation, true);
            }

            CancelAllQwenStreams();
            HandlePendingPredictResultAfterTTS();
            Log.Info(_isInterrupted ? "[Qwen-TTS] 已中断" : "[Qwen-TTS] 播放完成");
        }

        IEnumerator QwenPrefetchGeneratorLoop(
            List<QwenPreparedSentence> preparedSentences,
            string ttsBaseUrl,
            string refAudioPath,
            string promptText,
            bool audioPathCheck,
            List<QwenStreamingSentenceSession> sessions,
            Func<bool> isTranslationFinished,
            Action onCompleted)
        {
            try
            {
                for (int i = 0; i < preparedSentences.Count; i++)
                {
                    if (_isInterrupted)
                        break;

                    while (!preparedSentences[i].Ready)
                    {
                        if (_isInterrupted)
                            yield break;
                        if (isTranslationFinished != null && isTranslationFinished() && !preparedSentences[i].Ready)
                        {
                            Log.Warning($"[Qwen-TTS] 第 {i + 1}/{preparedSentences.Count} 句未拿到翻译结果，停止后续生成");
                            yield break;
                        }
                        yield return null;
                    }

                    var session = new QwenStreamingSentenceSession
                    {
                        Index = i,
                        EmotionTag = preparedSentences[i].EmotionTag,
                        SubtitleText = preparedSentences[i].SubtitleText,
                        TtsText = preparedSentences[i].TtsText,
                        Player = new TTSClient.StreamingAudioPlayer(),
                        Cancellation = new CancellationTokenSource(),
                    };

                    RegisterQwenStreamCancellation(session.Cancellation);
                    session.StreamTask = TTSClient.StreamQwenTtsAsync(
                        ttsBaseUrl,
                        session.TtsText,
                        refAudioPath,
                        promptText,
                        session.Player,
                        Logger,
                        session.Cancellation.Token,
                        audioPathCheck,
                        $"Qwen-TTS #{i + 1}/{preparedSentences.Count}");

                    sessions.Add(session);
                    Log.Info($"[Qwen-TTS] 第 {i + 1}/{preparedSentences.Count} 句已启动生成");

                    while (!session.Player.Completed
                        && string.IsNullOrEmpty(session.Player.ErrorMessage))
                    {
                        if (_isInterrupted)
                        {
                            session.Cancellation?.Cancel();
                            yield break;
                        }
                        yield return null;
                    }

                    Log.Info($"[Qwen-TTS] 第 {i + 1}/{preparedSentences.Count} 句生成循环结束：completed={session.Player.Completed}, error={session.Player.ErrorMessage ?? "null"}");
                }
            }
            finally
            {
                onCompleted?.Invoke();
            }
        }

        IEnumerator QwenTranslationPrefetchLoop(
            List<StreamingSentenceTask> sentences,
            bool enableTranslation,
            string deeplxUrl,
            string sourceLang,
            string translateTargetLang,
            List<QwenPreparedSentence> preparedSentences,
            Action onCompleted)
        {
            try
            {
                for (int i = 0; i < sentences.Count; i++)
                {
                    if (_isInterrupted)
                        break;

                    string emotion = string.IsNullOrWhiteSpace(sentences[i].EmotionTag) ? "Think" : sentences[i].EmotionTag.Trim();
                    string subtitle = ResponseParser.StripEmotionPrefix(sentences[i].SubtitleText);
                    if (string.IsNullOrWhiteSpace(subtitle))
                        subtitle = sentences[i].SubtitleText.Trim();

                    string ttsText = subtitle;
                    if (enableTranslation && !string.IsNullOrEmpty(deeplxUrl))
                    {
                        string translatedText = null;
                        yield return StartCoroutine(DeepLXTranslate(
                            deeplxUrl,
                            subtitle,
                            sourceLang,
                            translateTargetLang,
                            Logger,
                            (result) => translatedText = result
                        ));

                        if (!string.IsNullOrEmpty(translatedText))
                        {
                            ttsText = translatedText;
                            Log.Info($"[Qwen-TTS] 第 {i + 1}/{sentences.Count} 句翻译成功：{subtitle} → {ttsText}");
                        }
                        else
                        {
                            Log.Warning($"[Qwen-TTS] 第 {i + 1}/{sentences.Count} 句翻译失败，使用原文兜底");
                        }
                    }

                    preparedSentences[i].EmotionTag = emotion;
                    preparedSentences[i].SubtitleText = subtitle;
                    preparedSentences[i].TtsText = ttsText;
                    preparedSentences[i].Ready = true;
                    Log.Info($"[Qwen-TTS] 第 {i + 1}/{sentences.Count} 句翻译预取完成");
                }
            }
            finally
            {
                onCompleted?.Invoke();
            }
        }

        IEnumerator PlayQwenSessionWhenReady(
            QwenStreamingSentenceSession session,
            bool shouldSwitchEmotion,
            UnityEngine.UI.Text myText)
        {
            float requiredBufferSeconds = QwenStreamStartBufferSeconds;

            while (!session.Player.Initialized && string.IsNullOrEmpty(session.Player.ErrorMessage) && !session.Player.Completed)
            {
                if (_isInterrupted)
                {
                    session.Cancellation?.Cancel();
                    yield break;
                }
                if (myText != null)
                {
                    myText.text = $"正在流式生成语音... 缓冲 {session.Player.BufferedSeconds:F2}/{requiredBufferSeconds:F1}s";
                    BringOverlayToFront(myText);
                }
                yield return null;
            }

            if (!string.IsNullOrEmpty(session.Player.ErrorMessage) || !session.Player.Initialized)
            {
                Log.Warning($"[Qwen-TTS] 第 {session.Index + 1} 句启动失败：{session.Player.ErrorMessage ?? "未收到任何音频块"}");
                if (myText != null)
                {
                    myText.text = ResponseParser.InsertLineBreaks(session.SubtitleText, 25);
                    BringOverlayToFront(myText);
                    myText.color = Color.white;
                }
                yield return StartCoroutine(PlayEmotionOnly(session.EmotionTag, shouldSwitchEmotion));
                yield break;
            }

            while (session.Player.BufferedSeconds < requiredBufferSeconds
                && !session.Player.Completed
                && string.IsNullOrEmpty(session.Player.ErrorMessage))
            {
                if (_isInterrupted)
                {
                    session.Cancellation?.Cancel();
                    yield break;
                }
                if (myText != null)
                {
                    myText.text = $"正在缓冲语音... {session.Player.BufferedSeconds:F2}/{requiredBufferSeconds:F1}s";
                    BringOverlayToFront(myText);
                }
                yield return null;
            }

            if (session.Player.BufferedSamples <= 0)
            {
                Log.Warning($"[Qwen-TTS] 第 {session.Index + 1} 句无可播放音频，按无语音回退");
                if (myText != null)
                {
                    myText.text = ResponseParser.InsertLineBreaks(session.SubtitleText, 25);
                    BringOverlayToFront(myText);
                    myText.color = Color.white;
                }
                yield return StartCoroutine(PlayEmotionOnly(session.EmotionTag, shouldSwitchEmotion));
                yield break;
            }

            if (session.Player.Completed && session.Player.BufferedSeconds < requiredBufferSeconds)
            {
                Log.Info($"[Qwen-TTS] 第 {session.Index + 1} 句短句长度 {session.Player.BufferedSeconds:F2}s，直接按完整短句播放");
            }

            AudioClip streamClip = AudioClip.Create(
                $"QwenTtsStream_{session.Index}",
                session.Player.SampleRate * 120,
                session.Player.Channels,
                session.Player.SampleRate,
                true,
                session.Player.Read);

            if (myText != null)
            {
                myText.text = ResponseParser.InsertLineBreaks(session.SubtitleText, 25);
                BringOverlayToFront(myText);
                myText.color = Color.white;
            }

            yield return StartCoroutine(PlayQwenSessionClip(streamClip, session, shouldSwitchEmotion));
        }

        IEnumerator PlayQwenSessionClip(
            AudioClip voiceClip,
            QwenStreamingSentenceSession session,
            bool shouldSwitchEmotion)
        {
            if (voiceClip == null)
                yield break;

            _audioSource.clip = voiceClip;

            if (shouldSwitchEmotion)
                yield return StartCoroutine(BeginEmotionAnimation(session.EmotionTag));

            double scheduledStartDspTime = AudioSettings.dspTime + QwenStreamPlaybackLeadSeconds;
            _audioSource.PlayScheduled(scheduledStartDspTime);
            float playbackTailSeconds = GetQwenPlaybackTailSeconds(session.Player.SampleRate);
            double scheduledEndDspTime = -1d;
            bool talkingStarted = false;
            Log.Info($"[Qwen-TTS] 第 {session.Index + 1} 句计划开始播放，buffer={session.Player.BufferedSeconds:F2}s");

            while (!_isInterrupted)
            {
                if (!talkingStarted && AudioSettings.dspTime >= scheduledStartDspTime)
                {
                    SetTalkingState(true);
                    talkingStarted = true;
                    Log.Info($"[Qwen-TTS] 第 {session.Index + 1} 句实际进入播放");
                }

                if (session.Player.Completed)
                {
                    long appendedSamples = session.Player.TotalAppendedSamples;
                    if (appendedSamples > 0 && session.Player.SampleRate > 0 && session.Player.Channels > 0)
                    {
                        float totalDurationSeconds = appendedSamples / (float)(session.Player.SampleRate * session.Player.Channels);
                        if (scheduledEndDspTime < 0d)
                        {
                            scheduledEndDspTime = scheduledStartDspTime + totalDurationSeconds + playbackTailSeconds;
                            Log.Info($"[Qwen-TTS] 第 {session.Index + 1} 句预计播放时长 {totalDurationSeconds:F2}s，计划结束时间窗口 {(float)(scheduledEndDspTime - scheduledStartDspTime):F2}s");
                        }

                        if (AudioSettings.dspTime >= scheduledEndDspTime)
                            break;
                    }
                }

                yield return null;
            }

            Log.Info($"[Qwen-TTS] 第 {session.Index + 1} 句播放结束");
            StopCurrentAudioPlayback();
        }

        IEnumerator BeginEmotionAnimation(string emotion)
        {
            if (GameBridge._heroineService == null || GameBridge._changeAnimSmoothMethod == null)
                yield break;

            if (emotion != "Drink")
            {
                GameBridge.CallNativeChangeAnim(250);
                yield return new WaitForSecondsRealtime(0.2f);
            }

            int animID = 1001;
            switch (emotion)
            {
                case "Happy": animID = 1001; break;
                case "Sad": animID = 1002; break;
                case "Fun": animID = 1003; break;
                case "Confused": animID = 1302; break;
                case "Agree": animID = 1301; break;
                case "Drink":
                    GameBridge.CallNativeChangeAnim(250);
                    yield return new WaitForSecondsRealtime(0.5f);
                    animID = 256;
                    break;
                case "Think": animID = 252; break;
                case "Wave":
                    animID = 5001;
                    GameBridge.CallNativeChangeAnim(animID);
                    yield return new WaitForSecondsRealtime(0.3f);
                    GameBridge.ControlLookAt(1.0f, 0.5f);
                    yield break;
            }

            GameBridge.CallNativeChangeAnim(animID);
        }

        IEnumerator PlayEmotionOnly(string emotion, bool shouldSwitchEmotion)
        {
            if (shouldSwitchEmotion)
                yield return StartCoroutine(BeginEmotionAnimation(emotion));
            else
                yield return new WaitForSecondsRealtime(0.1f);
        }

        /// <summary>
        /// 后台 TTS+ 翻译生成循环：逐句生成 TTS + DeepLX 翻译
        /// </summary>
        IEnumerator TTSWithTranslationGeneratorLoop(
            List<StreamingSentenceTask> sentences,
            string ttsBaseUrl,
            string targetLang,
            string refAudioPath,
            string promptText,
            string promptLang,
            bool audioPathCheck,
            Queue<AudioClip> audioQueue,
            Queue<string> subtitleQueue,
            Queue<string> emotionQueue,
            bool enableTranslation,
            string deeplxUrl,
            string sourceLang,
            string translateTargetLang)
        {
            for (int i = 0; i < sentences.Count; i++)
            {
                // 中断检查：停止生成剩余句子
                if (_isInterrupted)
                {
                    Log.Info($"[TTS 生成] 收到中断信号，停止生成（已完成 {i}/{sentences.Count} 句）");
                    yield break;
                }

                string sentenceEmotion = string.IsNullOrWhiteSpace(sentences[i].EmotionTag) ? "Think" : sentences[i].EmotionTag.Trim();
                // 防御性清洗：即使上游解析异常，也不让 [Emotion] ||| 标签流入翻译/TTS。
                string originalText = ResponseParser.StripEmotionPrefix(sentences[i].SubtitleText);  // 中文原文（字幕用）
                if (string.IsNullOrWhiteSpace(originalText))
                    originalText = sentences[i].SubtitleText.Trim();
                string ttsText = originalText;       // TTS 用文本，默认用原文兜底
                
                // 如果启用翻译，先请求 DeepLX 翻译（中文→日文），翻译结果送 TTS
                if (enableTranslation && !string.IsNullOrEmpty(deeplxUrl))
                {
                    string translatedText = null;
                    yield return StartCoroutine(DeepLXTranslate(
                        deeplxUrl,
                        originalText,
                        sourceLang,
                        translateTargetLang,
                        Logger,
                        (result) => translatedText = result
                    ));
                    
                    if (!string.IsNullOrEmpty(translatedText))
                    {
                        ttsText = translatedText;  // 翻译成功：日文送 TTS
                        Log.Info($"[翻译] 第 {i + 1}/{sentences.Count} 句翻译成功：{originalText} → {ttsText}");
                    }
                    else
                    {
                        Log.Warning($"[翻译] 第 {i + 1}/{sentences.Count} 句翻译失败，TTS 使用中文原文兜底");
                    }
                }
                
                // 字幕显示中文原文（带换行处理）
                subtitleQueue.Enqueue(ResponseParser.InsertLineBreaks(originalText, 25));
                emotionQueue.Enqueue(sentenceEmotion);
                
                // 生成 TTS 语音（使用翻译后的日文，或原文兜底）
                AudioClip clip = null;
                yield return StartCoroutine(TTSClient.DownloadVoiceWithRetry(
                    ttsBaseUrl + "/tts",
                    ttsText,
                    targetLang,
                    refAudioPath,
                    promptText,
                    promptLang,
                    Logger,
                    (c) => clip = c,
                    3,
                    30f,
                    audioPathCheck));
                
                if (clip != null)
                {
                    Log.Info($"[TTS+ 翻译] 第 {i + 1}/{sentences.Count} 句生成成功");
                }
                else
                {
                    Log.Warning($"[TTS+ 翻译] 第 {i + 1}/{sentences.Count} 句 TTS 生成失败");
                }
                
                audioQueue.Enqueue(clip);
            }
            Log.Info("[TTS+ 翻译] 所有句子生成完成");
        }

        /// <summary>
        /// DeepLX 翻译请求协程
        /// </summary>
        IEnumerator DeepLXTranslate(
            string deeplxUrl,
            string text,
            string sourceLang,
            string targetLang,
            BepInEx.Logging.ManualLogSource logger,
            System.Action<string> onComplete)
        {
            string jsonBody = "{ "
                + "\"text\": \"" + ResponseParser.EscapeJson(text) + "\", "
                + "\"source_language\": \"" + sourceLang + "\", "
                + "\"target_language\": \"" + targetLang + "\" }";
            
            logger.LogInfo($"[DeepLX] 请求翻译：{text}");
            
            using (UnityWebRequest request = UnityWebRequest.Post(deeplxUrl, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 10;
                
                yield return request.SendWebRequest();
                
                if (request.result == UnityWebRequest.Result.Success)
                {
                    // 解析 JSON 响应，提取 target_text
                    string jsonResponse = request.downloadHandler.text;
                    logger.LogInfo($"[DeepLX] 响应：{jsonResponse}");
                    
                    // 简单 JSON 解析：提取 target_text 字段
                    string targetText = ResponseParser.ExtractJsonValue(jsonResponse, "target_text");
                    onComplete?.Invoke(targetText);
                }
                else
                {
                    logger.LogWarning($"[DeepLX] 翻译失败：{request.error}");
                    onComplete?.Invoke(null);
                }
            }
        }

        IEnumerator TTSHealthCheckLoop()
        {
            // 委托给 TTSClient 的双向心跳实现
            yield return StartCoroutine(TTSClient.TTSHealthLoop(
                () => GetTtsBaseUrl(),
                () => GetCurrentTtsProvider(),
                Logger,
                (ready) => { _isTTSServiceReady = ready; }
            ));
        }

        IEnumerator DeepLXHealthCheckLoop()
        {
            // 缓存 WaitForSeconds，避免每次循环 new 分配
            var waitShort = new WaitForSeconds(5f);  // 未连接时
            var waitLong  = new WaitForSeconds(30f); // 已连接时

            // 在 translate URL 末尾直接拼 /health
            // 例：http://127.0.0.1:12393/translate/deeplx → http://127.0.0.1:12393/translate/deeplx/health
            string healthUrl = GetDeepLXUrl().TrimEnd('/') + "/health";

            bool lastState = false;

            while (true)
            {
                bool isReady = false;

                // GET 请求探测 /health，不使用返回 body
                using (UnityWebRequest req = UnityWebRequest.Get(healthUrl))
                {
                    req.timeout = 5;
                    yield return req.SendWebRequest();
                    isReady = req.result == UnityWebRequest.Result.Success;
                }

                // 只在状态变化时回调 + 打 Log
                if (isReady != lastState)
                {
                    lastState = isReady;
                    _isDeepLXServiceReady = isReady;
                    if (isReady)
                        Logger.LogInfo("[DeepLX Health] 服务已连接 ✅");
                    else
                        Logger.LogWarning("[DeepLX Health] 服务断开 ❌");
                }

                yield return isReady ? waitLong : waitShort;
            }
        }

        IEnumerator PlayAudioWithoutEmotionSwitch(AudioClip voiceClip)
        {
            if (voiceClip == null)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                HandlePendingPredictResultAfterTTS();
                yield break;
            }

            float clipDuration = voiceClip.length;
            _audioSource.clip = voiceClip;
            _audioSource.Play();
            SetTalkingState(true);

            // 保持和 PlayNativeAnimation 一致的播放缓冲。
            yield return new WaitForSecondsRealtime(clipDuration + 0.5f);

            if (_audioSource != null && _audioSource.isPlaying)
            {
                Log.Warning("等待结束，强制停止语音播放");
                _audioSource.Stop();
            }
            SetTalkingState(false);

            HandlePendingPredictResultAfterTTS();
        }

        private void HandlePendingPredictResultAfterTTS()
        {
            if (!_pendingPredictResult) return;
            _pendingPredictResult = false;
            _showPredictedReplies = true;
            StartCoroutine(ShowDebugLog($"[预测] TTS 完成，显示预测结果：{_predictedAngelReply.Length}字 / {_predictedDevilReply.Length}字"));
            Log.Info("[预测回复] TTS 播放完成，显示预测结果");
        }
        
        IEnumerator PlayNativeAnimation(string emotion, AudioClip voiceClip)
        {
            if (GameBridge._heroineService == null || GameBridge._changeAnimSmoothMethod == null) yield break;

            Log.Info($"[动画] 执行: {emotion}");
            float clipDuration = (voiceClip != null) ? voiceClip.length : 3.0f;
            // 1. 归位 (除了喝茶)
            if (emotion != "Drink")
            {
                GameBridge.CallNativeChangeAnim(250);
                yield return new WaitForSecondsRealtime(0.2f);
            }
            if (voiceClip != null)
            {
                // 2. 播放语音 + 动作
                Log.Info($">>> 语音({voiceClip.length:F1}s) + 动作");
                _audioSource.clip = voiceClip;
                _audioSource.Play();
                SetTalkingState(true);
            }
            else
            {
                Log.Info($">>> 无语音模式 (格式错误或TTS失败) + 动作");
                // 没声音就不播了，只做动作
                SetTalkingState(false);
            }
            int animID = 1001;

            switch (emotion)
            {
                case "Happy": animID = 1001; break;
                case "Sad": animID = 1002; break;
                case "Fun": animID = 1003; break;
                case "Confused": animID = 1302; break; // Frustration
                case "Agree": animID = 1301; break;

                case "Drink":
                    GameBridge.CallNativeChangeAnim(250);
                    yield return new WaitForSecondsRealtime(0.5f);
                    animID = 256; // DrinkTea
                    break;

                case "Think":
                    animID = 252; // Thinking
                    break;

                case "Wave":
                    animID = 5001;
                    GameBridge.CallNativeChangeAnim(animID);

                    // 等待抬手
                    yield return new WaitForSecondsRealtime(0.3f);
                    // 强制看玩家
                    GameBridge.ControlLookAt(1.0f, 0.5f);

                    // 等待动作或语音结束 (取长者)
                    float waitTime = Mathf.Max(clipDuration, 2.5f);
                    yield return new WaitForSecondsRealtime(waitTime);

                    // 归位
                    GameBridge.CallNativeChangeAnim(250);
                    GameBridge.RestoreLookAt();

                    SetTalkingState(false);
                    yield break; // 退出
            }

            // 执行通用动作
            GameBridge.CallNativeChangeAnim(animID);

            // 等待语音播完，增加0.5秒缓冲，以防止过早判断AI动作结束
            yield return new WaitForSecondsRealtime(clipDuration + 0.5f);

            // 恢复
            if (_audioSource != null && _audioSource.isPlaying) {
                // 即使等待时间到了，语音还在播放，就强制停止进行兜底
                Log.Warning("等待结束，强制停止语音播放");
                _audioSource.Stop();
            }
            GameBridge.RestoreLookAt();
            SetTalkingState(false);

            HandlePendingPredictResultAfterTTS();
        }

        // ================= 【新增录音控制】 =================
        void StartRecording()
        {
            Log.Info($"[Mic Debug] 检测到设备数量: {Microphone.devices.Length}");
            if (Microphone.devices.Length > 0)
            {
                foreach (var d in Microphone.devices)
                {
                    Log.Info($"[Mic Debug] 可用设备: {d}");
                }
            }
            // --------------------

            if (Microphone.devices.Length == 0)
            {
                Log.Error("未检测到麦克风！(Microphone.devices is empty)");
                // 可以在屏幕上显示个错误提示
                _playerInput = "[Error: No Mic Found]"; 
                return;
            }

            _microphoneDevice = Microphone.devices[0];
            _recordingClip = Microphone.Start(_microphoneDevice, false, MaxRecordingSeconds, RecordingFrequency);
            _isRecording = true;
            Log.Info($"开始录音: {_microphoneDevice}");
        }

        void StopRecordingAndRecognize()
        {
            if (!_isRecording) return;

            // 1. 停止录音
            int position = Microphone.GetPosition(_microphoneDevice);
            Microphone.End(_microphoneDevice);
            _isRecording = false;
            Log.Info($"停止录音，采样点: {position}");

            // 2. 剪裁有效音频 (去掉末尾的静音/空白部分)
            if (position <= 0) return; // 录音太短

            AudioClip validClip = AudioUtils.TrimAudioClip(_recordingClip, position);

            // 3. 编码并发送
            byte[] wavData = AudioUtils.EncodeToWAV(validClip);
            StartCoroutine(ASRWorkflow(wavData));
        }
        /// <summary>
        /// ASR 业务流：负责调度网络请求和后续的 AI 响应
        /// </summary>
        IEnumerator ASRWorkflow(byte[] wavData)
        {
            _isProcessing = true; // 锁定 UI
            string recognizedResult = "";

            // A. 调用 ApiService 只负责拿回文字
            yield return StartCoroutine(ASRClient.SendAudioToASR(
                wavData,
                _sovitsUrlConfig.Value,
                (text) => recognizedResult = text
            ));

            // B. 根据拿回的结果，在主类决定下一步业务走向
            if (!string.IsNullOrEmpty(recognizedResult))
            {
                Log.Info($"[Workflow] ASR 成功，开始进入 AI 思考流程: {recognizedResult}");

                // 这里触发 AI 处理流程
                yield return StartCoroutine(AIProcessRoutine(recognizedResult));
            }
            else
            {
                Log.Warning("[Workflow] ASR 未能识别到有效文本");
                _isProcessing = false; // 如果识别失败，在这里解锁 UI
            }
        }
        void OnApplicationQuit()
        {
            Log.Info("[Chill AI Mod] 退出中...");
            
            Log.Info("[Chill AI Mod] 正在停止TTS轮询...");
            if (_ttsHealthCheckCoroutine != null)
            {
                StopCoroutine(_ttsHealthCheckCoroutine);
                _ttsHealthCheckCoroutine = null;
            }
            if (_deeplxHealthCheckCoroutine != null)
            {
                StopCoroutine(_deeplxHealthCheckCoroutine);
                _deeplxHealthCheckCoroutine = null;
            }
            CancelAllQwenStreams();
            StopCurrentAudioPlayback();
            CleanupActiveUiState();
            _isProcessing = false;
            _aiProcessCoroutine = null;
        }

        // ================= 【中断 & URL 辅助方法】 =================

        /// <summary>
        /// 获取 DeepLX URL：XnneHangLab 模式下从 base URL 拼接，否则用配置值
        /// </summary>
        private string GetDeepLXUrl()
        {
            return _xnneHangLabChatBaseUrl.Value.TrimEnd('/') + "/translate/deeplx";
        }

        private string GetTtsBaseUrl()
        {
            return _xnneHangLabChatBaseUrl.Value;
        }

        private TTSClient.Provider GetCurrentTtsProvider()
        {
            NormalizeTtsProviderSelection();
            return TTSClient.GetProvider(_useGptSovitsTtsConfig.Value, _useFasterQwenTtsConfig.Value);
        }

        private void NormalizeTtsProviderSelection()
        {
            if (_useGptSovitsTtsConfig == null || _useFasterQwenTtsConfig == null)
                return;

            if (_useGptSovitsTtsConfig.Value == _useFasterQwenTtsConfig.Value)
            {
                _useGptSovitsTtsConfig.Value = true;
                _useFasterQwenTtsConfig.Value = false;
            }
        }

        private void ApplyTtsProviderToggleState(bool useGptSovits, bool useFasterQwenTts)
        {
            if (useGptSovits == useFasterQwenTts)
            {
                if (useGptSovits && !_useGptSovitsTtsConfig.Value)
                    useFasterQwenTts = false;
                else if (useFasterQwenTts && !_useFasterQwenTtsConfig.Value)
                    useGptSovits = false;
                else
                {
                    useGptSovits = true;
                    useFasterQwenTts = false;
                }
            }

            _useGptSovitsTtsConfig.Value = useGptSovits;
            _useFasterQwenTtsConfig.Value = useFasterQwenTts;
        }

        private void RegisterQwenStreamCancellation(CancellationTokenSource cancellation)
        {
            if (cancellation == null) return;
            if (!_activeQwenStreamCancellations.Contains(cancellation))
                _activeQwenStreamCancellations.Add(cancellation);
        }

        private void UnregisterQwenStreamCancellation(CancellationTokenSource cancellation, bool dispose = false)
        {
            if (cancellation == null) return;
            _activeQwenStreamCancellations.Remove(cancellation);
            if (dispose)
                cancellation.Dispose();
        }

        private void CancelAllQwenStreams()
        {
            for (int i = 0; i < _activeQwenStreamCancellations.Count; i++)
            {
                try { _activeQwenStreamCancellations[i].Cancel(); }
                catch { }
                try { _activeQwenStreamCancellations[i].Dispose(); }
                catch { }
            }
            _activeQwenStreamCancellations.Clear();
        }

        private void StopCurrentAudioPlayback()
        {
            if (_audioSource != null)
            {
                if (_audioSource.isPlaying)
                    _audioSource.Stop();
                _audioSource.clip = null;
            }
            GameBridge.RestoreLookAt();
            SetTalkingState(false);
        }

        private void TrackActiveUiState(
            Dictionary<GameObject, bool> uiStatusMap,
            GameObject overlayTextObj,
            GameObject originalTextObj)
        {
            _activeUiStatusMap = uiStatusMap;
            _activeOverlayTextObj = overlayTextObj;
            _activeOriginalTextObj = originalTextObj;
        }

        private void CleanupActiveUiState()
        {
            if (_activeUiStatusMap == null && _activeOverlayTextObj == null && _activeOriginalTextObj == null)
                return;

            UIHelper.RestoreUiStatus(_activeUiStatusMap, _activeOverlayTextObj, _activeOriginalTextObj);
            _activeUiStatusMap = null;
            _activeOverlayTextObj = null;
            _activeOriginalTextObj = null;
        }

        private void InterruptCurrentProcess(bool notifyBackend)
        {
            _isInterrupted = true;
            CancelAllQwenStreams();
            StopCurrentAudioPlayback();

            if (_aiProcessCoroutine != null)
            {
                StopCoroutine(_aiProcessCoroutine);
                _aiProcessCoroutine = null;
            }

            CleanupActiveUiState();
            _isProcessing = false;

            _showPredictedReplies = false;
            _pendingPredictResult = false;
            _predictedAngelReply = "";
            _predictedDevilReply = "";

            if (notifyBackend)
                StartCoroutine(PostInterruptSignal());
        }

        private float GetQwenPlaybackTailSeconds(int sampleRate)
        {
            if (sampleRate <= 0)
                return QwenStreamPlaybackTailSeconds;

            AudioSettings.GetDSPBufferSize(out int bufferLength, out int numBuffers);
            float dspBufferedSeconds = (bufferLength * Mathf.Max(1, numBuffers)) / (float)sampleRate;
            return Mathf.Max(QwenStreamPlaybackTailSeconds, dspBufferedSeconds + 0.08f);
        }

        private void SetTalkingState(bool speaking)
        {
            _isAISpeaking = speaking;
            if (GameBridge._cachedAnimator != null)
            {
                try
                {
                    if (GameBridge._cachedAnimator.GetBool("Enable_Talk") != speaking)
                        GameBridge._cachedAnimator.SetBool("Enable_Talk", speaking);
                }
                catch { }
            }
        }

        /// <summary>
        /// 获取 XnneHangLab Chat Completions URL
        /// </summary>
        private string GetChatUrl()
        {
            return _xnneHangLabChatBaseUrl.Value.TrimEnd('/') + "/memory/chat";
        }

        /// <summary>
        /// fire-and-forget：向 memory server 发送中断信号
        /// </summary>
        IEnumerator PostInterruptSignal()
        {
            string baseUrl = _xnneHangLabChatBaseUrl.Value;
            
            string url = baseUrl.TrimEnd('/') + "/memory/interrupt";
            Log.Info($"[中断] 发送 POST {url}");

            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(new byte[0]);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.ProtocolError)
                    Log.Warning($"[中断] POST 失败（忽略）: {req.error}");
                else
                    Log.Info($"[中断] POST 成功: {req.responseCode}");
            }
        }

        // ================= 【预测回复相关方法】 =================

        /// <summary>
        /// 显示调试日志到字幕（不生成 TTS）
        /// </summary>
        IEnumerator ShowDebugLog(string message, float displaySeconds = 3f)
        {
            // 获取游戏 UI 上下文
            GameObject canvas = GameObject.Find("Canvas");
            if (canvas == null) yield break;
            
            Transform originalTextTrans = canvas.transform.Find("StorySystemUI/MessageWindow/NormalTextParent/NormalTextMessage");
            if (originalTextTrans == null) yield break;
            
            GameObject originalTextObj = originalTextTrans.gameObject;
            GameObject parentObj = originalTextObj.transform.parent.gameObject;
            
            // 创建临时字幕
            GameObject debugTextObj = UIHelper.CreateOverlayText(parentObj);
            Text debugText = debugTextObj.GetComponent<Text>();
            BringOverlayToFront(debugText);
            
            // 显示日志
            debugText.text = message;
            debugText.color = new Color(1f, 1f, 0.5f); // 淡黄色
            
            Log.Info($"[DebugLog] {message}");
            
            // 显示指定时间
            yield return new WaitForSecondsRealtime(displaySeconds);
            
            // 清理
            UnityEngine.Object.Destroy(debugTextObj);
        }

        /// <summary>
        /// 触发预测回复：异步调用 LLM 生成预选回复
        /// </summary>
        IEnumerator TriggerPredictReply(string aiLastResponse)
        {
            _isPredicting = true;
            _showPredictedReplies = false;
            _pendingPredictResult = false;

            // 显示调试日志到字幕
            StartCoroutine(ShowDebugLog("[预测] 开始生成预测..."));
            Log.Info("[预测回复] 开始生成预测...");

            string contextPrompt = BuildContextPrompt();
            string fullUserPrompt = BuildPredictUserPrompt(contextPrompt, aiLastResponse);

            string angelReply = "";
            string devilReply = "";
            bool angelSuccess = false;
            bool devilSuccess = false;

            // 小天使：单独请求，单独提示词
            yield return StartCoroutine(RequestPredictedReply(
                "PredictAngel",
                _predictAngelPromptConfig.Value,
                fullUserPrompt,
                (reply, ok) =>
                {
                    angelReply = reply;
                    angelSuccess = ok;
                }));

            // 小恶魔：单独请求，单独提示词
            yield return StartCoroutine(RequestPredictedReply(
                "PredictDevil",
                _predictDevilPromptConfig.Value,
                fullUserPrompt,
                (reply, ok) =>
                {
                    devilReply = reply;
                    devilSuccess = ok;
                }));

            if (angelSuccess || devilSuccess)
            {
                _predictedAngelReply = angelReply;
                _predictedDevilReply = devilReply;

                if (_isAISpeaking)
                {
                    _pendingPredictResult = true;
                    StartCoroutine(ShowDebugLog($"[预测] 生成完成：{angelReply.Length}字 / {devilReply.Length}字，等待 TTS 完成后显示"));
                    Log.Info("[预测回复] TTS 播放中，等待播放完成后显示");
                }
                else
                {
                    _showPredictedReplies = true;
                    StartCoroutine(ShowDebugLog($"[预测] 生成完成并显示：{angelReply.Length}字 / {devilReply.Length}字"));
                    Log.Info("[预测回复] 预测完成并显示");
                }
            }
            else
            {
                StartCoroutine(ShowDebugLog("[预测] 两次请求都失败，未显示预选回复"));
            }

            _isPredicting = false;
        }

        /// <summary>
        /// 构建预测请求 UserPrompt（两次请求复用同一上下文）
        /// </summary>
        private string BuildPredictUserPrompt(string contextPrompt, string aiLastResponse)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(contextPrompt))
            {
                sb.Append(contextPrompt);
                if (!contextPrompt.EndsWith("\n"))
                    sb.AppendLine();
            }

            sb.AppendLine("Task: Predict the user's next message as ONE short chat sentence.");
            sb.AppendLine("Reply with sentence only. No JSON, no label, no explanation.");
            sb.AppendLine("Last AI response:");
            sb.AppendLine(aiLastResponse ?? "");
            return sb.ToString();
        }

        /// <summary>
        /// 单次预测请求：返回提取后的纯文本 content
        /// </summary>
        private IEnumerator RequestPredictedReply(
            string logHeader,
            string systemPrompt,
            string userPrompt,
            Action<string, bool> onComplete)
        {
            var requestContext = new LLMRequestContext
            {
                ApiUrl = _chatApiUrlConfig.Value,
                ApiKey = _apiKeyConfig.Value,
                ModelName = _modelConfig.Value,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                UseLocalOllama = _useOllama.Value,
                UseXnneHangLab = false,
                UseXnneHangLabChatServer = false,
                LogApiRequestBody = _logApiRequestBodyConfig.Value,
                ThinkMode = ThinkMode.Default,
                HierarchicalMemory = null,
                LogHeader = logHeader,
                FixApiPathForThinkMode = false,
                EnableTranslation = false,
                DeepLXUrl = "",
                TranslateTargetLang = ""
            };

            string rawResponse = "";
            bool success = false;

            yield return LLMClient.SendLLMRequest(
                requestContext,
                (response) =>
                {
                    rawResponse = response;
                    success = true;
                },
                (errorMsg, responseCode) =>
                {
                    StartCoroutine(ShowDebugLog($"[预测] {logHeader} API 错误：{errorMsg}"));
                    Log.Warning($"[预测回复] {logHeader} API 错误：{errorMsg} (Code: {responseCode})");
                    success = false;
                }
            );

            if (!success || string.IsNullOrEmpty(rawResponse))
            {
                onComplete?.Invoke("", false);
                yield break;
            }

            string content = ExtractPredictContent(rawResponse);
            if (string.IsNullOrEmpty(content))
            {
                onComplete?.Invoke("", false);
                yield break;
            }

            StartCoroutine(ShowDebugLog($"[预测] {logHeader} 提取 content: {content.Length}字"));
            onComplete?.Invoke(content, true);
        }

        /// <summary>
        /// 从模型响应里提取预测文本（只取 content/reply/message）
        /// </summary>
        private string ExtractPredictContent(string rawResponse)
        {
            if (string.IsNullOrEmpty(rawResponse))
                return "";

            string content = ResponseParser.ExtractJsonValue(rawResponse, "content");
            if (string.IsNullOrEmpty(content))
                content = ResponseParser.ExtractJsonValue(rawResponse, "reply");
            if (string.IsNullOrEmpty(content))
                content = ResponseParser.ExtractJsonValue(rawResponse, "message");
            if (string.IsNullOrEmpty(content))
                content = rawResponse;

            content = content.Replace("\r", " ").Replace("\n", " ").Trim();
            content = content.Trim('"', '\'', ' ', '\t');
            content = StripPredictLabelPrefix(content, "小天使");
            content = StripPredictLabelPrefix(content, "小恶魔");
            content = StripPredictLabelPrefix(content, "小惡魔");
            content = StripPredictLabelPrefix(content, "angel");
            content = StripPredictLabelPrefix(content, "devil");
            return content.Trim();
        }

        private string StripPredictLabelPrefix(string content, string label)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(label))
                return content;

            if (content.StartsWith(label + "：", StringComparison.OrdinalIgnoreCase))
                return content.Substring(label.Length + 1).Trim();
            if (content.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
                return content.Substring(label.Length + 1).Trim();
            return content;
        }

        /// <summary>
        /// 更新预测上下文（维护 2-3 轮对话，4-6 条消息）
        /// </summary>
        private void UpdatePredictContext(string userMessage, string aiResponse)
        {
            // 添加用户消息
            if (!string.IsNullOrEmpty(userMessage))
            {
                _predictContext.Add("User: " + userMessage);
            }
            
            // 添加 AI 回复
            if (!string.IsNullOrEmpty(aiResponse))
            {
                _predictContext.Add("AI: " + aiResponse);
            }
            
            // 如果超出最大长度，删除最旧的 2 条
            while (_predictContext.Count > MaxContextMessages)
            {
                // 删除最旧的 2 条（保持对话完整性）
                if (_predictContext.Count >= 2)
                {
                    _predictContext.RemoveAt(0);
                    _predictContext.RemoveAt(0);
                }
                else
                {
                    _predictContext.RemoveAt(0);
                }
            }
            
            Log.Info($"[预测上下文] 更新后长度={_predictContext.Count}");
        }

        /// <summary>
        /// 构建预测用的上下文字符串
        /// </summary>
        private string BuildContextPrompt()
        {
            if (_predictContext.Count == 0)
            {
                return "";
            }
            
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Conversation history:");
            foreach (string msg in _predictContext)
            {
                sb.AppendLine(msg);
            }
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// 在屏幕右面的按钮最下面添加一个AI聊天按钮
        /// </summary>
        private void AddAIChatButtonToRightIcons()
        {
            try
            {
                // 查找RightIcons容器（参考UIRearrangePatch.cs中的路径）
                string rightIconsPath = "Paremt/Canvas/UI/MostFrontArea/TopIcons";
                GameObject rightIcons = GameObject.Find(rightIconsPath);
                
                if (rightIcons == null)
                {
                    Log.Warning($"找不到RightIcons容器: {rightIconsPath}");
                    return;
                }
                
                // 创建新按钮游戏对象
                _aiChatButton = new GameObject("IconAIChat_Button");
                
                // 设置为RightIcons的子节点
                _aiChatButton.transform.SetParent(rightIcons.transform, false);
                
                // 添加RectTransform组件
                RectTransform rectTransform = _aiChatButton.AddComponent<RectTransform>();
                
                // 获取RightIcons中其他按钮的大小作为参考
                float buttonSize = 60f; // 默认大小
                if (rightIcons.transform.childCount > 0)
                {
                    RectTransform firstButtonRect = rightIcons.transform.GetChild(0).GetComponent<RectTransform>();
                    if (firstButtonRect != null)
                    {
                        buttonSize = Mathf.Max(firstButtonRect.sizeDelta.x, firstButtonRect.sizeDelta.y);
                    }
                }
                
                // 设置按钮大小
                rectTransform.sizeDelta = new Vector2(buttonSize, buttonSize);

                // 添加Image组件
                Image image = _aiChatButton.AddComponent<Image>();

                try
                {
                    image.sprite = EmbeddedSpriteLoader.Load("ai_chat.png");
                    image.color = Color.white;
                    image.preserveAspect = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"加载内置图片失败: {ex}");
                    image.color = Color.red; // 兜底
                }


                // 添加Button组件
                Button button = _aiChatButton.AddComponent<Button>();
                
                // 添加点击事件
                button.onClick.AddListener(() =>
                {
                    _showInputWindow = !_showInputWindow;
                });
                
                // 设置按钮位置到最底部
                // 获取所有子节点并按位置排序
                List<RectTransform> children = new List<RectTransform>();
                for (int i = 0; i < rightIcons.transform.childCount; i++)
                {
                    RectTransform childRect = rightIcons.transform.GetChild(i).GetComponent<RectTransform>();
                    if (childRect != null)
                    {
                        children.Add(childRect);
                    }
                }
                
                // 按Y坐标排序（Unity UI中Y值越小越靠下）
                children.Sort((a, b) => a.anchoredPosition.y.CompareTo(b.anchoredPosition.y));
                
                // 如果有其他按钮，将新按钮放在最下面
                if (children.Count > 1) // 至少有一个其他按钮
                {
                    RectTransform lowestButton = children[3]; // 第一个是最下面的
                    float spacing = 10f;
                    rectTransform.anchoredPosition = new Vector2(
                        lowestButton.anchoredPosition.x,
                        lowestButton.anchoredPosition.y - (buttonSize + spacing)
                    );
                }
                else
                {
                    // 如果是第一个按钮，居中放置
                    rectTransform.anchoredPosition = Vector2.zero;
                }
                
                // 设置锚点和pivot，使其与其他按钮一致
                rectTransform.anchorMin = new Vector2(1f, 1f);
                rectTransform.anchorMax = new Vector2(1f, 1f);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);
                
                _aiChatButtonAdded = true;
                Log.Info($"✅ AI聊天按钮已添加到RightIcons容器");
            }
            catch (Exception ex)
            {
                Log.Error($"添加AI聊天按钮失败: {ex.Message}");
            }
        }
    }
}
