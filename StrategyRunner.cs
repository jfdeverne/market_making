using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KGClasses;
using StrategyLib;
using System.IO;
using System.Reflection;

namespace Detail
{
    public enum CtrlTypes
    {
        CTRL_C_EVENT,
        CTRL_BREAK_EVENT,
        CTRL_CLOSE_EVENT,
        CTRL_LOGOFF_EVENT = 5,
        CTRL_SHUTDOWN_EVENT
    }

    public enum Side
    {
        NONE = 0,
        BUY = 1,
        SELL = 2
    }

    public enum Source
    {
        NEAR = 0,
        FAR = 1,
    }
}

namespace Throttler
{
    public class Throttler
    {
        private int mMaxVolume;
        private TimeSpan mTime;
        private readonly Queue<Tuple<DateTime, int>> mOrderVolumes;
        int mCurrentVolume;

        public Throttler(int maxVolume, TimeSpan time)
        {
            mMaxVolume = maxVolume;
            mTime = time;
            mOrderVolumes = new Queue<Tuple<DateTime, int>>();
            mCurrentVolume = 0;
        }

        public void updateMaxVolume(int maxVolume)
        {
            mMaxVolume = maxVolume;
        }

        public void updateTimespan(int seconds)
        {
            TimeSpan ts = new TimeSpan(0,0,0,0,seconds*1000);
            mTime = ts;
        }

        public bool addTrade(int volume)
        {
            CleanExpired();

            if (mCurrentVolume + volume > mMaxVolume)
            {
                Console.WriteLine("trade blocked by throttler");
                return false;
            }

            mOrderVolumes.Enqueue(new Tuple<DateTime, int>(DateTime.UtcNow, volume));
            mCurrentVolume += volume;

            return true;
        }

        private void CleanExpired()
        {
            while (mOrderVolumes.Count > 0 && DateTime.UtcNow - mOrderVolumes.Peek().Item1 > mTime)
            {
                var expired = mOrderVolumes.Dequeue();
                mCurrentVolume -= expired.Item2;
            }
        }
    }
}

namespace StrategyRunner
{
    struct VariableInfo
    {
        public FieldInfo fi;
        public int strategyIndex;
        public List<int> arrIndices;
    }

    class StrategyRunner
    {
        public static API API;
        [DllImport("Kernel32")]
        public static extern bool SetConsoleCtrlHandler(HandlerRoutine Handler, bool Add);

        public delegate bool HandlerRoutine(Detail.CtrlTypes CtrlType);
        private static HandlerRoutine _ConsoleCtrlCheck = ConsoleCtrlCheck;
        static private HandlerRoutine ctrlCHandler;

        private Dictionary<int, Strategy> strategies;

        public static List<VariableInfo> vars = new List<VariableInfo>();


        StrategyRunner()
        {
            string gitVersion = String.Empty;
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StrategyRunner." + "gitHEADversion.txt");
            if (stream != null)
            {
                StreamReader reader = new StreamReader(stream);
                if (reader != null)
                {
                    gitVersion = reader.ReadToEnd();
                }
            }
            strategies = new Dictionary<int, Strategy>();
            //setConsoleWindowVisibility(false, Console.Title);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(GenericErrorHandler);

            ctrlCHandler = new HandlerRoutine(ConsoleCtrlCheck);
            SetConsoleCtrlHandler(ctrlCHandler, true);
            API = new API();

            API.OnDeal += API_OnDeal;
            API.OnProcessMD += API_OnProcessMD;
            API.OnOrder += API_OnOrder;
            API.OnStatusChanged += API_OnStatusChanged;
            API.OnFlush += API_OnFlush;
            API.OnImprovedCM += API_OnImprovedCM;
            API.OnConnect += API_OnConnect;
            API.OnSystemTradingMode += API_OnSystemTradingMode;
            API.OnPurgedOrder += API_OnPurgedOrder;
            API.OnStrategyVariableUpdate += API_OnStrategyVariableUpdate;
            API.OnStrategyVariableRequest += API_OnStrategyVariableRequest;
            API.OnStrategyParamRequest += API_OnStrategyParamRequest;
            API.OnStrategyParamUpdate += API_OnStrategyParamUpdate;

            bool ret = API.Init();
            if (!ret)
            {
                API.Log("Failed loading config files", true);
                return;
            }

            API.Log(String.Format("git version={0}", gitVersion));
            API.Log("mm-1.1.0");

            API.Connect();
        }

        private void API_OnStrategyParamUpdate(string paramName, string paramVal)
        {
            int sInd = paramName.IndexOf('.') - 1;
            int strategyIndex = Int32.Parse(paramName.Substring(1, sInd));
            if (!strategies.ContainsKey(strategyIndex))
            {
                API.SendToRemote("[Variable ERR:" + paramName + "] strategy index doesnt exist", KGConstants.EVENT_ERROR);
                return;
            }
            paramName = paramName.Substring(sInd + 2);
            P.SetValue(paramName, paramVal);

            foreach (var strategy in strategies.Values)
                strategy.OnParamsUpdate();

            API.Log("Setting parameter value:" + paramName + "," + paramVal);
        }

        private void API_OnStrategyParamRequest()
        {
            string line = P.GetParamsStr();
            API.UpdateParams(line);
        }

        private void API_OnStrategyVariableRequest(string varName, string arrayIndicesStr)
        {
            try
            {
                VariableInfo var;
                if (varName == "R")
                {
                    vars.Clear();
                    return;
                }
                int sInd = varName.IndexOf('.') - 1;
                int strategyIndex = Int32.Parse(varName.Substring(1, sInd));
                if (!strategies.ContainsKey(strategyIndex))
                {
                    API.SendToRemote("[Variable ERR:" + varName + "] strategy index doesnt exist", KGConstants.EVENT_ERROR);
                    return;
                }
                varName = varName.Substring(sInd + 2);
                Type x = typeof(Strategy);
                FieldInfo fi = x.GetField(varName);
                List<int> Indices = new List<int>();
                if (arrayIndicesStr != "")
                {
                    try
                    {
                        Array obj = null;
                        string[] parts = arrayIndicesStr.Split(',');
                        if (fi.FieldType.IsArray)
                        {
                            Object s = strategies[strategyIndex];
                            obj = (Array)fi.GetValue(s);
                        }
                        else
                            API.SendToRemote("VAR-DISPLAY-ERR: variable isn't array:" + varName, KGConstants.EVENT_ERROR);

                        for (int k = 0; k < parts.Length; k++)
                        {
                            if (parts[k].Contains(":"))
                            {
                                if (parts[k] == ":")
                                { // get all indices
                                    for (int kk = 0; kk < obj.Length; kk++)
                                        Indices.Add(kk);
                                }
                                else if (parts[k][0] == ':')
                                {
                                    int second = Int32.Parse(parts[k][1].ToString());
                                    for (int kk = 0; kk <= second; kk++)
                                        Indices.Add(kk);
                                }
                                else if (parts[k][parts[k].Length - 1] == ':')
                                {

                                    int first = Int32.Parse(parts[k].Substring(0, parts[k].Length - 1));
                                    for (int kk = first; kk < obj.Length; kk++)
                                        Indices.Add(kk);
                                }
                                else
                                {
                                    int colonInd = parts[k].IndexOf(':');
                                    int first = Int32.Parse(parts[k].Substring(0, colonInd));
                                    int second = Int32.Parse(parts[k].Substring(colonInd + 1));
                                    for (int kk = first; kk <= second; kk++)
                                        Indices.Add(kk);
                                }
                            }
                            else
                            {
                                int index = Int32.Parse(parts[k]);
                                Indices.Add(index);
                            }

                        } // end- for       
                    }
                    catch (Exception)
                    {
                        API.SendToRemote("VAR-DISPLAY-ERR: array format error:" + varName + "@" + arrayIndicesStr, KGConstants.EVENT_ERROR);
                    }
                }
                //Strategy s = strategies[strategyIndex];
                //fi.GetValue(s);
                var.fi = fi;
                var.strategyIndex = strategyIndex;
                var.arrIndices = Indices;
                vars.Add(var);
            }
            catch (Exception e)
            {
                API.Log("Exception OnVariableRequest: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnStrategyVariableUpdate()
        {
            try
            {
                string line = "";
                for (int i = 0; i < vars.Count; i++)
                {
                    FieldInfo fi = vars[i].fi;
                    Object s = strategies[vars[i].strategyIndex];
                    if (fi.FieldType.IsArray)
                    {
                        Array obj = (Array)fi.GetValue(s);
                        //int index = (int)varIndices[i];
                        //line += obj.GetValue(index) + " ";
                        List<int> oo = vars[i].arrIndices;
                        for (int k = 0; k < oo.Count; k++)
                        {
                            if (k > 0)
                                line += ";";
                            int index = (int)oo[k];
                            line += index + "=" + Math.Round(Double.Parse(obj.GetValue(index).ToString()), 4);
                        }
                        line += " ";
                    }
                    else
                        line += fi.GetValue(s) + " ";
                }
                API.UpdateVar(line);
            }
            catch (Exception e)
            {
                API.Log("Exception OnVariableUpdate: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnSystemTradingMode(char c)
        {
            foreach (KeyValuePair<int, Strategy> kv in strategies)
            {
                kv.Value.OnSystemTradingMode(ref c);
            }
        }

        private void API_OnPurgedOrder(KGOrder ord)
        {
            if (strategies.ContainsKey(ord.stgID))
            {
                strategies[ord.stgID].OnPurgedOrder(ord);
            }
        }

        private void API_OnConnect()
        {
            API.Log("Connected");
            string pathQuoter = Directory.GetCurrentDirectory() + "/quoter_config";
            var filesQuoter = Directory.GetFiles(pathQuoter, "*.xml", SearchOption.TopDirectoryOnly);

            string pathBV = Directory.GetCurrentDirectory() + "/bv_config";
            var filesBV = Directory.GetFiles(pathBV, "*.xml", SearchOption.TopDirectoryOnly);

            foreach (var file in filesQuoter)
            {
                Strategy s = new Quoter(API, file);
                strategies[s.stgID] = s;
            }

            foreach (var file in filesBV)
            {
                Strategy s = new BV(API, file);
                strategies[s.stgID] = s;
            }
        }

        private static bool ConsoleCtrlCheck(Detail.CtrlTypes ctrlType)
        {
            API.Log("ERR:" + "Closing the application:" + ctrlType.ToString(), true);
            System.Environment.Exit(-1);
            return true;
        }

        static void GenericErrorHandler(object sender, UnhandledExceptionEventArgs e)
        {
            API.Log("ERR:" + "GenericErrorHandler - Exiting...", true);
            System.Environment.Exit(-1);
        }

        private void API_OnStatusChanged(int status, int stgID)
        {
            if (strategies.ContainsKey(stgID))
            {
                strategies[stgID].OnStatusChanged(status);
            }
        }

        private void API_OnFlush()
        {
            foreach (KeyValuePair<int, Strategy> kv in strategies)
            {
                kv.Value.OnFlush();
            }
        }

        private void API_OnOrder(KGOrder ord)
        {
            if (strategies.ContainsKey(ord.stgID))
            {
                strategies[ord.stgID].OnOrder(ord);
            }
        }

        private void API_OnProcessMD(VIT vi, int stgID)
        {
            if (strategies.ContainsKey(stgID))
            {
                strategies[stgID].OnProcessMD(vi);
            }
        }

        private void API_OnImprovedCM(int index, double CMPrice)
        {
            foreach (KeyValuePair<int, Strategy> kv in strategies)
            {
                kv.Value.OnImprovedCM(index, CMPrice);
            }
        }

        private void API_OnDeal(KGDeal deal)
        {
            if (strategies.ContainsKey(deal.stgID))
            {
                strategies[deal.stgID].OnDeal(deal);
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            StrategyRunner runner = new StrategyRunner();
            return;
        }
    }
}