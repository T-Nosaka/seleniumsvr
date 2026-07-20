using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace seleniumsvr
{
    /// <summary>
    /// ログ処理
    /// </summary>
    public sealed class Logger
    {
        /// <summary>
        /// ログ文字エンコード
        /// </summary>
        public static Encoding encode = Encoding.UTF8;

        #region ログ内容
        /// <summary>
        /// ログ内容
        /// </summary>
        private class LogContent
        {
            /// <summary>
            /// ログ発生時刻
            /// </summary>
            private DateTime m_time = DateTime.Now;

            /// <summary>
            /// ログ内容
            /// </summary>
            private string _logText;

            /// <summary>
            /// ログタイプ
            /// </summary>
            private LogType _logType;

            /// <summary>
            /// コンストラクタ
            /// </summary>
            /// <param name="aLogText">ログ内容</param>
            /// <param name="isAppendHeader">ヘッダフラグ</param>
            /// <param name="aLogType">ログタイプ</param>
            public LogContent(string aLogText, LogType aLogType)
            {
                m_time = DateTime.Now;
                _logText = aLogText;
                _logType = aLogType;
            }

            /// <summary>
            /// ログ出力
            /// </summary>
            /// <param name="writer"></param>
            public void Write(StringWriter writer)
            {
                var logTime = m_time;

                var logTypeStr = (_logType != LogType.Nothing) ? string.Format("[{0}] ", _logType.ToString()) : string.Empty;

                using (var reader = new StringReader(_logText))
                {
                    var txt = string.Empty;
                    while ((txt = reader.ReadLine()) != null)
                    {
                        writer.WriteLine(string.Format("[{0:yyyy/MM/dd HH:mm:ss.fff}] : {2}{1}", logTime, txt, logTypeStr));
                    }
                }

                writer.Flush();
            }

        }
        #endregion

        /// <summary>
        /// コンストラクタ
        /// </summary>
        private Logger()
        {
        }

        /// <summary>
        /// ファイル書き込みロック用
        /// </summary>
        private static Object m_logwrite = new Object();

        /// <summary>
        /// ファイル書き込みロック
        /// </summary>
        public static Object LogLock
        {
            get
            {
                return m_logwrite;
            }
        }

        /// <summary>
        /// ログリスト
        /// </summary>
        private static List<LogContent> m_loglist = new List<LogContent>();

        /// <summary>
        /// ログディレクトリ作成
        /// </summary>
        private static string CreateLogDirectory(string logPath)
        {
            //ディレクトリ確認
            var logDirectory = Path.GetDirectoryName(logPath)!;
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            return logDirectory;
        }

        /// <summary>
        /// ロガーライタースレッド
        /// </summary>
        private static void WriterThread()
        {
            try
            {
                //ログがない場合、処理しない
                if (m_loglist.Count == 0)
                    return;

                try
                {
                    //ログ出力
                    LogWrite(m_loglist);
                }
                catch (Exception ex)
                {
                    //ディレクトリ
                    var logPath = GetLogFilename();
                    CreateLogDirectory(logPath);

                    try
                    {
                        //ファイル出力
                        lock (m_logwrite)
                        {
                            using (var writer = new StreamWriter(logPath, true, encode))
                            {
                                writer.WriteLine("ロガー例外発生:" + ex.ToString());
                                writer.Flush();
                            }
                        }
                    }
                    catch { }
                }
                finally
                {
                    m_loglist.Clear();
                }
            }
            catch (Exception ex)
            {
                //ディレクトリ
                var logPath = GetLogFilename();
                CreateLogDirectory(logPath);

                //ファイル出力
                lock (m_logwrite)
                {
                    using (var writer = new StreamWriter(logPath, true, encode))
                    {
                        writer.WriteLine("ロガー例外終了:" + ex.ToString());
                        writer.Flush();
                    }
                }
            }
        }

        /// <summary>
        /// ログ出力処理
        /// </summary>
        /// <param name="syncloglist"></param>
        private static void LogWrite(List<LogContent> syncloglist)
        {
            //ディレクトリ
            var logPath = GetLogFilename();
            var logDirectory = CreateLogDirectory(logPath);

            //ファイル出力
            lock (m_logwrite)
            {
                using (var writer = new StreamWriter(logPath, true, encode))
                {
                    foreach (var contents in syncloglist)
                    {
                        using (var str = new StringWriter())
                        {
                            contents.Write(str);

                            writer.Write(str.ToString());
                        }
                    }

                    writer.Flush();
                }

                if (new FileInfo(logPath).Length > _logFileMaxSize)
                {
                    //ファイルスイッチ発生
                    OnSwitchLogFile();
                }
            }
        }

        /// <summary>
        /// 蓄積時間
        /// </summary>
        private static int m_pool_interval = 1000;

        /// <summary>
        /// 蓄積時間プロパティ
        /// </summary>
        public static int PoolInterval
        {
            get
            {
                return m_pool_interval;
            }
            set
            {
                m_pool_interval = value;
            }
        }

        /// <summary>
        /// ロガースレッド
        /// </summary>
        private static Thread? m_loggerthread = null;

        /// <summary>
        /// 排他的ログ追加
        /// </summary>
        /// <param name="log"></param>
        private static void AddLog(LogContent log)
        {
            lock (m_loglist)
            {
                //溜まりすぎ対策
                if (m_loglist.Count > 100000)
                    return;

                m_loglist.Add(log);

                if (m_loggerthread == null)
                {
                    m_loggerthread = new Thread(() =>
                    {
                        Thread.Sleep(PoolInterval);

                        lock (m_loglist)
                        {
                            WriterThread();

                            m_loggerthread = null;
                        }
                    });
                    m_loggerthread.Start();
                }
            }
        }

        /// <summary>
        /// LOG_DIRECTORY
        /// </summary>
		private const string LOG_DIRECTORY = "Log";

        /// <summary>
        /// ログファイル分割サイズ
        /// </summary>
        private static int _logFileMaxSize = 1000000;

        /// <summary>
        /// ログファイル分割サイズ
        /// </summary>
        public static int MaxSize
        {
            get
            {
                return _logFileMaxSize;
            }
            set
            {
                _logFileMaxSize = value;
            }
        }

        #region ログファイル名処理
        /// <summary>
        /// ログファイル名を作成
        /// </summary>
        /// <returns></returns>
        private static string GetLogFilename()
        {
            return GetLogFilename(DateTime.Now);
        }

        /// <summary>
		/// ログファイル名を取得
		/// </summary>
		/// <param name="aDate"></param>
		/// <returns></returns>
		public static string GetLogFilename(DateTime aDate)
        {
            if (m_current_date != aDate.Date || m_current_filename == string.Empty)
            {
                //初期ファイル、或いは、異なる対象日時
                //対象日時にて、空きファイル名を探す
                int count = 1;
                while (true)
                {
                    var filename = GetLogFilename(aDate, count);
                    var nextFilename = GetLogFilename(aDate, count + 1);

                    if (File.Exists(nextFilename) || (File.Exists(filename) && (new FileInfo(filename)).Length > _logFileMaxSize))
                    {
                        count++;
                        continue;
                    }

                    //現在ファイル名更新
                    m_current_date = aDate.Date;
                    m_current_filename = filename;

                    return filename;
                }
            }
            else
            {
                return m_current_filename;
            }
        }

        /// <summary>
        /// 現在のファイル名
        /// </summary>
        private static string m_current_filename = string.Empty;

        /// <summary>
        /// 現在のファイル名の対象日時
        /// </summary>
        private static DateTime m_current_date = DateTime.Now.Date;

        /// <summary>
        /// ログファイルスイッチ発生
        /// </summary>
        private static void OnSwitchLogFile()
        {
            //1年前ログ削除
            DeleteLastYearLogs(GetLogDirectory());

            m_current_filename = string.Empty;
        }

        /// <summary>
        /// ログファイル名を取得
        /// </summary>
        /// <param name="aDate"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public static string GetLogFilename(DateTime aDate, int index)
        {
            return Path.Combine(GetLogDirectory(), string.Format("{0:yyyyMMdd}_{1}.log", aDate, index));
        }

        /// <summary>
        /// ログディレクトリを取得
        /// </summary>
        /// <returns></returns>
        public static string GetLogDirectory()
        {
            if (m_logdirectory == string.Empty)
            {
                m_logdirectory = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!, LOG_DIRECTORY);
            }
            return m_logdirectory;
        }

        /// <summary>
        /// ログディレクトリ
        /// </summary>
        public static string m_logdirectory = string.Empty;

        /// <summary>
        /// ログディレクトリ設定
        /// </summary>
        /// <param name="path"></param>
        public static void SetLogDirectory(string path)
        {
            m_logdirectory = path;
        }

        #endregion

        /// <summary>
        /// プロパティログのフォーマット
        /// </summary>
        private const string PROPERTY_LOG_TEMPLATE = "{0} : [{1}]";

        /// <summary>
        /// ログを出力する
        /// </summary>
        /// <param name="aLogTxt">出力するテキスト</param>
        public static void Log(string aLogTxt)
        {
            Log(aLogTxt, LogType.Nothing);
        }

        /// <summary>
        /// ログを出力する
        /// </summary>
        /// <param name="aLogTxt">出力するテキスト</param>
        public static void Log(Exception ex)
        {
            LogException(ex);
        }

        /// <summary>
        /// ログの種類を指定してログを出力
        /// </summary>
        /// <param name="aLogTxt">出力するテキスト</param>

        /// <param name="aLogType">ヘッダに追加するログの種類</param>
        public static void Log(string aLogTxt, LogType aLogType)
        {
            var log = new LogContent(aLogTxt, aLogType);
            new Thread(() =>
            {
                AddLog(log);
            }).Start();
        }

        /// <summary>
        /// 前年度分のログを削除します
        /// </summary>
        /// <param name="aLogDirectory">ログを検索するディレクトリ</param>
        private static void DeleteLastYearLogs(string aLogDirectory)
        {
            try
            {
                var dirInfo = new DirectoryInfo(aLogDirectory);
                var fileInfoList = dirInfo.GetFiles("*.log");
                foreach (var info in fileInfoList)
                {
                    try
                    {
                        if (info.Name.Length < 12) continue;
                        var dateNumbers = info.Name.Substring(0, 8);
                        var date = string.Format("{0}/{1}/{2}", dateNumbers.Substring(0, 4), dateNumbers.Substring(4, 2), dateNumbers[6..]);
                        var fileTime = DateTime.Parse(date);

                        var span = DateTime.Now - fileTime.AddYears(1);
                        if (span.TotalDays >= 0)
                        {
                            info.Delete();
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// ログを出力する。Debugのときのみ有効
        /// </summary>
        /// <remarks>
        /// Release環境で無効になる以外は<seealso cref="Logger.Log"/>と同一です。
        /// </remarks>
        /// <param name="aLogTxt">出力するテキスト</param>
        [Conditional("DEBUG")]
        public static void DebugLog(string aLogTxt)
        {
            Log(aLogTxt);
        }

        /// <summary>
        /// 指定したオブジェクトのパブリックプロパティとパブリックフィールドの情報をログに出力する
        /// </summary>
        /// <remarks>
        /// デバッグ用のメソッドで、実行するとログファイルに対して指定したオブジェクトの全ての
        /// パブリックプロパティとパブリックフィールドの情報を出力します。<br/>
        /// このメソッドの実行は時間がかかるため、デバッグ目的以外には使用しないでください。
        /// </remarks>
        /// <param name="anObj">情報を出力するオブジェクト</param>
        public static void Dump(object anObj)
        {
            try
            {
                using (var writer = new StringWriter())
                {
                    writer.WriteLine("Instance Member Dump ... Type : {0}", anObj.GetType().FullName);
                    writer.WriteLine("[Properties]");
                    var pInfoList = anObj.GetType().GetProperties();
                    foreach (var pInfo in pInfoList)
                    {
                        writer.WriteLine(PROPERTY_LOG_TEMPLATE, pInfo.Name, GetDumpString(pInfo, anObj));
                    }

                    writer.WriteLine("[Fields]");
                    var fInfoList = anObj.GetType().GetFields();
                    foreach (var fInfo in fInfoList)
                    {
                        writer.WriteLine(PROPERTY_LOG_TEMPLATE, fInfo.Name, GetDumpString(fInfo, anObj));
                    }

                    Log(writer.GetStringBuilder().ToString());
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        /// <summary>
        /// FieldInfoおよびPropertyInfoからデータを取り出し、ログに吐き出す文字列を作る
        /// </summary>
        /// <param name="info"></param>
        /// <param name="instance"></param>
        /// <returns></returns>
        private static string? GetDumpString(MemberInfo info, object instance)
        {
            try
            {
                object? obj = null;
                if (info is FieldInfo)
                {
                    var method = info.GetType().GetMethod("GetValue", new Type[] { typeof(object) })!;
                    obj = method.Invoke(info, new object[] { instance })!;
                }
                else if (info is PropertyInfo)
                {
                    var pInfos = ((PropertyInfo)info).GetIndexParameters();
                    if (pInfos != null && pInfos.Length > 0) return string.Empty;

                    var method = info.GetType().GetMethod("GetValue", new Type[] { typeof(object), typeof(object[]) })!;
                    obj = method.Invoke(info, new object?[] { instance, null })!;
                }

                if (obj is Array && ((Array)obj).Rank < 3)
                {
                    var builder = new StringBuilder();
                    var ar = obj as Array;
                    if (ar!.Rank == 1)
                    {
                        builder.Append("{");
                        for (int i = 0; i < ar.GetLength(0); i++)
                        {
                            if (i != 0) builder.Append(",");
                            var val = ar.GetValue(i);
                            builder.Append((val == null) ? "null" : val!.ToString());
                        }
                        builder.Append("}");
                    }
                    else
                    {
                        builder.Append("{");
                        for (int i = 0; i < ar.GetLength(0); i++)
                        {
                            builder.Append("{");
                            for (int j = 0; j < ar.GetLength(1); j++)
                            {
                                if (j != 0) builder.Append(",");
                                var val = ar.GetValue(i, j);
                                builder.Append((val == null) ? "null" : val!.ToString());
                            }
                            builder.Append("}");
                        }
                        builder.Append("}");
                    }
                    return builder.ToString();
                }
                else
                {
                    return (obj == null) ? "null" : obj!.ToString();
                }
            }
            catch
            {
                throw;
            }
        }

        /// <summary>
        /// 例外をログに出力する
        /// </summary>
        /// <remarks>
        /// 例外の全内容をログに出力します。<br/>
        /// 明示的に例外を発生させて処理を分岐させている場合以外は必ずこのメソッドを呼び出して
        /// ログを出力するようにしてください。
        /// </remarks>
        /// <param name="ex">出力する例外</param>
        public static void LogException(Exception? ex)
        {
            try
            {
                using (var writer = new StringWriter())
                {
                    int depth = 0;

                    while (ex != null)
                    {
                        // ログの文字列を作成します
                        writer.WriteLine(string.Format("[Depth : {0}]", ++depth));
                        writer.WriteLine(string.Format("Type    : {0}", ex.GetType().FullName));
                        writer.WriteLine(string.Format("Message : {0}", ex.Message));
                        writer.WriteLine("StackTrace :");
                        writer.WriteLine(ex.StackTrace);

                        // InnerExceptionがある限り繰り返します
                        ex = ex.InnerException;

                        if (depth > 10) break;
                    }

                    // ログを出力します
                    Log(writer.GetStringBuilder().ToString(), LogType.Debug);
                }
            }
            catch { }
        }

        /// <summary>
        /// バイト列をログに出力
        /// </summary>
        /// <remarks>
        /// デバッグ用のメソッドです。
        /// </remarks>
        /// <param name="aData"></param>
        [Conditional("DEBUG")]
        public static void LogByteArray(byte[] aData)
        {
            var builder = new StringBuilder("[");
            foreach (var b in aData)
            {
                if (builder.Length != 1) builder.Append(" ");
                builder.Append(string.Format("{0:X2}", b));
            }
            builder.Append("]");
            Log(builder.ToString());
        }

        /// <summary>
        /// 呼び出しメソッド取得
        /// ※このメソッドは、スタック数と数えない
        /// </summary>
        /// <param name="iCount">スタック位置(0で、自身のメソッドとなる)</param>
        /// <returns></returns>
        public static string CallMethod(int iCount)
        {
            iCount++;

            var st = new StackTrace(true);
            if (st.FrameCount <= iCount)
                return "";
            var sf = st.GetFrame(iCount)!;

            return sf.GetMethod()!.DeclaringType!.ToString() + ":" + sf.GetMethod();
        }
    }

    /// <summary>
    /// ログの種類を指定するための列挙子
    /// </summary>
    public enum LogType
    {
        /// <summary>
        /// 指定なし
        /// </summary>
        Nothing,

        /// <summary>
        /// デバッグログ
        /// </summary>
        Debug,

        /// <summary>
        /// ソケット通信ログ
        /// </summary>
        Socket,

        /// <summary>
        /// プロトコル通信ログ
        /// </summary>
        Protocol,

        /// <summary>
        /// 操作ログ
        /// </summary>
        Operation,

        /// <summary>
        /// システム操作ログ（SystemOperationが長ったらしいので…）
        /// </summary>
        System,

        /// <summary>
        /// データベース操作ログ
        /// </summary>
        DB,

        /// <summary>
        /// OSイベントログ
        /// </summary>
        OS
    }
}
