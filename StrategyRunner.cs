using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KGClasses;
using StrategyLib;
using System.IO;
using System.Reflection;
using Mapack;
using System.Xml.Linq;
using System.Globalization;
using System.Text;

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

        public void updateTimespan(double seconds)
        {
            double ms = seconds * 1000;
            TimeSpan ts = new TimeSpan(0, 0, 0, 0, (int)ms);
            mTime = ts;
        }

        public bool addTrade(int volume)
        {
            CleanExpired();

            if (mCurrentVolume + volume > mMaxVolume)
            {
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

    public class EurexThrottler
    {
        private int mMaxOrderCount;
        private TimeSpan mTime;
        private readonly Queue<DateTime> mOrderTimestamps;
        int mCurrentCount;

        public EurexThrottler(int maxOrderCount, TimeSpan time)
        {
            mMaxOrderCount = maxOrderCount;
            mTime = time;
            mOrderTimestamps = new Queue<DateTime>();
            mCurrentCount = 0;
        }

        public void updateMaxVolume(int maxOrderCount)
        {
            mMaxOrderCount = maxOrderCount;
        }

        public void updateTimespan(double seconds)
        {
            double ms = seconds * 1000;
            TimeSpan ts = new TimeSpan(0, 0, 0, 0, (int)ms);
            mTime = ts;
        }

        public bool addTrade()
        {
            CleanExpired();

            if (mCurrentCount++ > mMaxOrderCount)
            {
                return false;
            }

            mOrderTimestamps.Enqueue(DateTime.UtcNow);

            return true;
        }

        private void CleanExpired()
        {
            while (mOrderTimestamps.Count > 0)
            {
                try
                {
                    if (DateTime.UtcNow - mOrderTimestamps.Peek() > mTime)
                    {
                        mOrderTimestamps.Dequeue();
                        mCurrentCount--;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        }
    }
}

namespace VolumeDetector
{
    public class VolumeDetector
    {
        private int mTriggerVolume;
        private int mTriggerTradeCount;
        private TimeSpan mTime;
        private readonly Queue<Tuple<DateTime, int>> mVolumePerTimestamp;
        int mCurrentVolume;
        double mLastPrice = -11;

        public VolumeDetector(int triggerVolume, int triggerTradeCount, TimeSpan time)
        {
            mTriggerVolume = triggerVolume;
            mTriggerTradeCount = triggerTradeCount;
            mTime = time;
            mVolumePerTimestamp = new Queue<Tuple<DateTime, int>>();
            mCurrentVolume = 0;
        }

        public void updateTriggerVolume(int triggerVolume)
        {
            mTriggerVolume = triggerVolume;
        }

        public void updateTriggerTradeCount(int triggerTradeCount)
        {
            mTriggerTradeCount = triggerTradeCount;
        }

        public void updateTimespan(double seconds)
        {
            double ms = seconds * 1000;
            TimeSpan ts = new TimeSpan(0, 0, 0, 0, (int)ms);
            mTime = ts;
        }

        public bool addTrade(int volume, double price)
        {
            if (mLastPrice == -11)
            {
                mLastPrice = price;
            }
            else if (price != mLastPrice)
            {
                mVolumePerTimestamp.Clear();
                mCurrentVolume = 0;
            }

            CleanExpired();

            if (mCurrentVolume + volume >= mTriggerVolume  && mVolumePerTimestamp.Count >= mTriggerTradeCount)
            {
                return true; //BF trigger
            }

            mVolumePerTimestamp.Enqueue(new Tuple<DateTime, int>(DateTime.UtcNow, volume));
            mCurrentVolume += volume;

            return false;
        }

        private void CleanExpired()
        {
            while (mVolumePerTimestamp.Count > 0 && DateTime.UtcNow - mVolumePerTimestamp.Peek().Item1 > mTime)
            {
                var expired = mVolumePerTimestamp.Dequeue();
                mCurrentVolume -= expired.Item2;
            }
        }
    }
}

public class Box
{
    public int holding;
    public int[] indices = new int[4];
    public double ICM;
    public double targetPrice;
    public int linkedStgID;
    public Box(int[] Indices, int Holding = 0)
    {
        holding = Holding;
        for (int i = 0; i < Indices.Length; i++)
            indices[i] = Indices[i];
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
        int NBoxes;
        int NOutrights;
        IMatrix V;
        IMatrix VVTInv;
        IMatrix VTVVTInv;
        IMatrix beta;
        int[] boxIndices;
        int[] outrightIndices;
        IMatrix boxTargetPrices; //y
        IMatrix outrightICMs; //xICM
        IMatrix outrightTargetPrices; //x = xICM + VTVVTInv*(y-V*xICM)
        Dictionary<int, double> outrightTargetPricesMap;

        List<int> allOutrightIndices;
        IMatrix boxHoldings;
        List<int> quoteIndices;
        List<int> quoteFarIndices;
        List<int> leanIndices;
        List<Box> boxes;

        public static API API;
        [DllImport("Kernel32")]
        public static extern bool SetConsoleCtrlHandler(HandlerRoutine Handler, bool Add);

        public delegate bool HandlerRoutine(Detail.CtrlTypes CtrlType);
        private static HandlerRoutine _ConsoleCtrlCheck = ConsoleCtrlCheck;
        static private HandlerRoutine ctrlCHandler;

        private Dictionary<int, Strategy> strategies;

        public static List<VariableInfo> vars = new List<VariableInfo>();

        Dictionary<string, int> volumePerInstrument;

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

            volumePerInstrument = new Dictionary<string, int>();

            strategies = new Dictionary<int, Strategy>();
            outrightTargetPricesMap = new Dictionary<int, double>();
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

            API.Connect();
        }

        private void API_OnStrategyParamUpdate(string paramName, string paramVal)
        {
            try
            {
                int sInd = paramName.IndexOf('.') - 1;
                int strategyIndex = Int32.Parse(paramName.Substring(1, sInd));

                if (strategyIndex == 0)
                {
                    paramName = paramName.Substring(sInd + 2);
                    P.SetValue(paramName, paramVal);
                    
                    foreach (var strategy in strategies)
                    {
                        strategy.Value.OnGlobalParamsUpdate();
                    }
                    
                    return;
                }

                if (!strategies.ContainsKey(strategyIndex))
                {
                    API.SendToRemote("[Variable ERR:" + paramName + "] strategy index doesnt exist", KGConstants.EVENT_ERROR);
                    return;
                }

                paramName = paramName.Substring(sInd + 2);
                strategies[strategyIndex].OnParamsUpdate(paramName, paramVal);

                API.Log("Setting parameter value:" + paramName + "," + paramVal);
            }
            catch (Exception e)
            {
                API.Log("Exception OnParamUpdate: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnStrategyParamRequest()
        {
            try
            {
                string line = P.GetParamsStr();
                API.UpdateParams(line);
            }
            catch (Exception e)
            {
                API.Log("Exception OnParamRequest: " + e.ToString() + "," + e.StackTrace);
            }
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
            try
            {
                foreach (KeyValuePair<int, Strategy> kv in strategies)
                {
                    kv.Value.OnSystemTradingMode(ref c);
                }
            }
            catch (Exception e)
            {
                API.Log("Exception OnSystemTradingMode: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnPurgedOrder(KGOrder ord)
        {
            try
            {
                if (strategies.ContainsKey(ord.stgID))
                {
                    strategies[ord.stgID].OnPurgedOrder(ord);
                }
            }
            catch (Exception e)
            {
                API.Log("Exception OnPurgedOrder: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnConnect()
        {
            try
            {
                API.Log("Connected");
                string pathQuoter = Directory.GetCurrentDirectory() + "/quoter.xml";
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string pathBackup = Directory.GetCurrentDirectory() + "/ZZZ_backup_quoter_" + timestamp + ".xml";

                File.Copy(pathQuoter, pathBackup, overwrite: true);

                var doc = XDocument.Load(pathQuoter);

                allOutrightIndices = new List<int>();
                quoteIndices = new List<int>();
                quoteFarIndices = new List<int>();
                leanIndices = new List<int>();
                boxes = new List<Box>();

                int ii = 0;

                foreach (var quoter in doc.Descendants("Quoter"))
                {
                    QuoterConfig config = new QuoterConfig
                    {
                        width = (double)quoter.Element("width"),
                        size = (int)quoter.Element("size"),
                        leanInstrument = (string)quoter.Element("leanInstrument"),
                        quoteInstrument = (string)quoter.Element("quoteInstrument"),
                        icsInstrument = (string)quoter.Element("ics"),
                        asymmetricQuoting = (bool?)quoter.Element("asymmetricQuoting"),
                        defaultBaseSpread = (double?)quoter.Element("defaultBaseSpread"),
                        limitPlusSize = (int?)quoter.Element("limitPlusSize"),
                        nonLeanLimitPlusSize = (int?)quoter.Element("nonLeanLimitPlusSize"),
                        crossVenueInstruments = new List<string>(),
                        correlatedInstruments = new List<string>()
                    };

                    foreach (var hedgeInstrument in quoter.Elements("hedgeInstrument"))
                    {
                        if (hedgeInstrument.Attribute("class").Value == "correlated")
                        {
                            config.correlatedInstruments.Add((string)hedgeInstrument);
                        }
                        else if (hedgeInstrument.Attribute("class").Value == "crossVenue")
                        {
                            config.crossVenueInstruments.Add((string)hedgeInstrument);
                        }
                    }

                    config.crossVenueInstruments.Add(config.quoteInstrument);

                    var leanEl = quoter.Element("leanInstrument");
                    if (leanEl.Attribute("class").Value == "correlated")
                    {
                        config.correlatedInstruments.Add(config.leanInstrument);
                    }
                    else if (leanEl.Attribute("class").Value == "crossVenue")
                    {
                        config.crossVenueInstruments.Add(config.leanInstrument);
                    }

                    Strategy s = new Quoter(API, config);
                    strategies[s.stgID] = s;
                }

                string pathBV = Directory.GetCurrentDirectory() + "/bv.xml";

                string pathBackupBV = Directory.GetCurrentDirectory() + "/ZZZ_backup_bv_" + timestamp + ".xml";
                File.Copy(pathBV, pathBackupBV, overwrite: true);

                var docBV = XDocument.Load(pathBV);


                ii = 0;

                foreach (var bv in docBV.Descendants("BV"))
                {
                    BVConfig config = new BVConfig
                    (
                        (string)bv.Element("nearInstrument"),
                        (string)bv.Element("farInstrument"),
                        (string)bv.Element("leanInstrument"),
                        (int?)bv.Element("limitPlusSize"),
                        (int?)bv.Element("nonLeanLimitPlusSize"),
                        (double?)bv.Element("defaultBaseSpread")
                    );

                    foreach (var hedgeInstrument in bv.Elements("hedgeInstrument"))
                    {
                        if (hedgeInstrument.Attribute("class").Value == "correlated")
                        {
                            config.correlatedInstruments.Add((string)hedgeInstrument);
                        }
                        else if (hedgeInstrument.Attribute("class").Value == "crossVenue")
                        {
                            config.crossVenueInstruments.Add((string)hedgeInstrument);
                        }
                    }

                    config.crossVenueInstruments.Add(config.nearInstrument);

                    var leanEl = bv.Element("leanInstrument");
                    if (leanEl.Attribute("class").Value == "correlated")
                    {
                        config.correlatedInstruments.Add(config.leanInstrument);
                    }
                    else if (leanEl.Attribute("class").Value == "crossVenue")
                    {
                        config.crossVenueInstruments.Add(config.leanInstrument);
                    }

                    var farEl = bv.Element("farInstrument");
                    if (farEl.Attribute("class").Value == "correlated")
                    {
                        config.correlatedInstruments.Add(config.farInstrument);
                    }
                    else if (farEl.Attribute("class").Value == "crossVenue")
                    {
                        config.crossVenueInstruments.Add(config.farInstrument);
                    }

                    Strategy s = new BV(API, config);
                    strategies[s.stgID] = s;
                }

                foreach (var bv in docBV.Descendants("LimitBV"))
                {
                    BVConfig config = new BVConfig
                    (
                        (string)bv.Element("nearInstrument"),
                        (string)bv.Element("farInstrument"),
                        (string)bv.Element("leanInstrument"),
                        (int?)bv.Element("limitPlusSize"),
                        (int?)bv.Element("nonLeanLimitPlusSize"),
                        (double?)bv.Element("defaultBaseSpread")
                    );

                    foreach (var hedgeInstrument in bv.Elements("hedgeInstrument"))
                    {
                        if (hedgeInstrument.Attribute("class").Value == "correlated")
                        {
                            config.correlatedInstruments.Add((string)hedgeInstrument);
                        }
                        else if (hedgeInstrument.Attribute("class").Value == "crossVenue")
                        {
                            config.crossVenueInstruments.Add((string)hedgeInstrument);
                        }
                    }

                    config.crossVenueInstruments.Add(config.nearInstrument);

                    var leanEl = bv.Element("leanInstrument");
                    if (leanEl.Attribute("class").Value == "correlated")
                    {
                        config.correlatedInstruments.Add(config.leanInstrument);
                    }
                    else if (leanEl.Attribute("class").Value == "crossVenue")
                    {
                        config.crossVenueInstruments.Add(config.leanInstrument);
                    }

                    var farEl = bv.Element("farInstrument");
                    if (farEl.Attribute("class").Value == "correlated")
                    {
                        config.correlatedInstruments.Add(config.farInstrument);
                    }
                    else if (farEl.Attribute("class").Value == "crossVenue")
                    {
                        config.crossVenueInstruments.Add(config.farInstrument);
                    }

                    Strategy s = new LimitBV(API, config);
                    strategies[s.stgID] = s;

                    //if (API.GetSecurityNumber(s.quoteIndex, 0).Length > 9) //NOT OUTRIGHT
                    if (s.boxTargetPrice != 0) //CAREFUL - MUST NOT PUT AN EXACT 0.0 IN THE XML
                    {
                        quoteIndices.Add(s.quoteIndex);
                        quoteFarIndices.Add(s.farIndex);
                        leanIndices.Add(s.leanIndex);
                        strategies[s.stgID].linkedBoxIndex = ii; //LINKING THE RELEVANT BOX ENTRY IN THE boxes ARRAYS
                        API.SetBoxTargetPrice(s.stgID, s.boxTargetPrice);
                        Combo combos = API.GetCombos(quoteIndices[ii]);
                        Combo leanCombos = API.GetCombos(leanIndices[ii]);




                        boxIndices = new int[4]; //leg1, leg2 of 1st calendar spread, then leg1 and leg2 of the 2nd cal spread
                        boxIndices[0] = (combos.spreadList[0].buyLeg) % API.n;
                        boxIndices[1] = (combos.spreadList[0].sellLeg) % API.n;
                        boxIndices[2] = (leanCombos.spreadList[0].buyLeg) % API.n;
                        boxIndices[3] = (leanCombos.spreadList[0].sellLeg) % API.n;
                        for (int i = 0; i <= 3; i++)
                            if (!allOutrightIndices.Contains(boxIndices[i]))
                                allOutrightIndices.Add(boxIndices[i]);

                        boxes.Add(new Box(boxIndices));
                        boxes[ii].targetPrice = API.GetBoxTargetPrice(s.stgID);
                        boxes[ii].linkedStgID = s.stgID;
                        ii++;
                    }
                }

                NBoxes = quoteIndices.Count;
                boxHoldings = new Matrix(NBoxes, 1);
                //outrightIndices = new int[2 * (NBoxes + 1)];
                NOutrights = allOutrightIndices.Count;
                outrightIndices = new int[NOutrights];

                V = new Matrix(NBoxes, NOutrights);
                beta = new Matrix(NOutrights, 1);
                boxTargetPrices = new Matrix(NBoxes, 1);
                outrightICMs = new Matrix(NOutrights, 1);
                outrightTargetPrices = new Matrix(NOutrights, 1);


                for (int i = 0; i < NBoxes; i++)
                {

                    Combo combos = API.GetCombos(quoteIndices[i]);
                    Combo leanCombos = API.GetCombos(leanIndices[i]);

                    //int placeInAllOutrights = allOutrightIndices.FindIndex(combos.spreadList[0].buyLeg % API.n);
                    V[i, i] = 1;
                    V[i, i + 1] = -1;
                    V[i, i + NBoxes + 1] = -1;
                    V[i, i + NBoxes + 2] = 1;

                    //WE PRESUME THAT i's sellLeg is (i+1)'s buyLeg:
                    if (i == 0)
                    {
                        outrightIndices[0] = combos.spreadList[0].buyLeg % API.n; 
                        outrightIndices[NBoxes + 1] = leanCombos.spreadList[0].buyLeg % API.n;
                        outrightTargetPricesMap.Add(outrightIndices[0], 0);
                        outrightTargetPricesMap.Add(outrightIndices[NBoxes + 1], 0);
                    }
                    outrightIndices[i + 1] = combos.spreadList[0].sellLeg % API.n;
                    outrightIndices[i + 1 + NBoxes + 1] = leanCombos.spreadList[0].sellLeg % API.n;
                    outrightTargetPricesMap.Add(outrightIndices[i+1], 0);
                    outrightTargetPricesMap.Add(outrightIndices[i + 1 + NBoxes + 1], 0);
                }

                if (1 == 1)
                {
                    for (int j = 0; j < NBoxes; j++)
                    {
                        Combo combos = API.GetCombos(quoteIndices[j]);
                        Combo farCombos = API.GetCombos(quoteFarIndices[j]);
                        Combo leanCombos = API.GetCombos(leanIndices[j]);
                        beta[j, 0] = API.GetOutrightPos(combos.spreadList[0].buyLeg) + API.GetOutrightPos(farCombos.spreadList[0].buyLeg);
                        beta[j + 1, 0] = API.GetOutrightPos(combos.spreadList[0].sellLeg) + API.GetOutrightPos(farCombos.spreadList[0].sellLeg);
                        beta[j + NBoxes + 1, 0] = API.GetOutrightPos(leanCombos.spreadList[0].buyLeg);
                        beta[j + NBoxes + 2, 0] = API.GetOutrightPos(leanCombos.spreadList[0].sellLeg);
                    }
                    VVTInv = (V.Multiply(V.Transpose())).Inverse;
                    boxHoldings = VVTInv.Multiply(V.Multiply(beta));
                    VTVVTInv = V.Transpose().Multiply(VVTInv);
                    for (int j = 0; j < NBoxes; j++)
                        boxes[j].holding = (int)boxHoldings[j, 0];
                }
            }
            catch (Exception e)
            {
                API.Log("Exception OnConnect: " + e.ToString() + "," + e.StackTrace);
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
            API.Log("GenericErrorHandler - Exiting... Reason:" + (e.ExceptionObject as Exception).Message + "::-->" + (e.ExceptionObject as Exception).StackTrace, true);
            System.Environment.Exit(-1);
        }
        private void API_OnStatusChanged(int status, int stgID)
        {
            try
            {
                if (strategies.ContainsKey(stgID))
                {
                    strategies[stgID].OnStatusChanged(status);
                }
            }
            catch (Exception e)
            {
                API.Log("Exception OnStatusChanged: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnFlush()
        {
            try
            {
                foreach (KeyValuePair<int, Strategy> kv in strategies)
                {
                    kv.Value.OnFlush();
                }

                StringBuilder csvContent = new StringBuilder();

                csvContent.AppendLine("instrument,volume");

                foreach (var pair in volumePerInstrument)
                {
                    csvContent.AppendLine($"{(pair.Key)},{pair.Value}");
                }

                File.WriteAllText("volumes.csv", csvContent.ToString());
            }
            catch (Exception e)
            {
                API.Log("Exception OnFlush: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnOrder(KGOrder ord)
        {
            try
            {
                if (strategies.ContainsKey(ord.stgID))
                {
                    strategies[ord.stgID].OnOrder(ord);
                }
            }
            catch (Exception e)
            {
                API.Log("Exception OnOrder: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnProcessMD(VIT vi, int stgID)
        {
            try
            {
                if (strategies[stgID].linkedBoxIndex > -1)
                {
                    boxes[strategies[stgID].linkedBoxIndex].targetPrice = API.GetBoxTargetPrice(stgID);
                    strategies[stgID].boxTargetPrice = boxes[strategies[stgID].linkedBoxIndex].targetPrice;
                    strategies[stgID].correlatedSpreadTargetPrice = strategies[stgID].boxTargetPrice;
                }
                else
                {
                    for (int i = 0; i < NBoxes; i++)
                    {
                        boxes[i].targetPrice = API.GetBoxTargetPrice(boxes[i].linkedStgID);
                        boxIndices = boxes[i].indices;
                        boxes[i].ICM = API.GetImprovedCM(boxIndices[0]) - API.GetImprovedCM(boxIndices[1]) - (API.GetImprovedCM(boxIndices[2]) - API.GetImprovedCM(boxIndices[3]));
                        if (Math.Abs(boxes[i].ICM - boxes[i].targetPrice) < 0.1) //PRECAUTION AGAINST OUTLAYERS
                            boxes[i].targetPrice = P.targetPriceDriftFactor * boxes[i].targetPrice + (1 - P.targetPriceDriftFactor) * boxes[i].ICM;
                        boxTargetPrices[i, 0] = boxes[i].targetPrice;
                        API.SetBoxTargetPrice(boxes[i].linkedStgID, boxes[i].targetPrice);
                    }
                    for (int i = 0; i < outrightIndices.Length; i++)
                    {
                        outrightICMs[i, 0] = API.GetImprovedCM(outrightIndices[i]);
                    }

                    outrightTargetPrices = outrightICMs.Addition(VTVVTInv.Multiply(boxTargetPrices.Subtraction(V.Multiply(outrightICMs))));
                    for (int i = 0; i < outrightIndices.Length; i++)
                        outrightTargetPricesMap[outrightIndices[i]] = outrightTargetPrices[i,0];

                    if (strategies.ContainsKey(stgID))
                    {
                        double targetBoxPrice = 0;
                        if (strategies[stgID].correlatedIndices.Count > 0)
                        {
                            Combo firstCombos = API.GetCombos(strategies[stgID].quoteIndex);
                            Combo secondCombos = API.GetCombos(strategies[stgID].correlatedIndices[0]);
                            if ((firstCombos.spreadList.Count > 0) && (secondCombos.spreadList.Count > 0))
                            {
                                targetBoxPrice += outrightTargetPricesMap[firstCombos.spreadList[0].buyLeg % API.n];
                                targetBoxPrice -= outrightTargetPricesMap[firstCombos.spreadList[0].sellLeg % API.n];
                                targetBoxPrice -= outrightTargetPricesMap[secondCombos.spreadList[0].buyLeg % API.n];
                                targetBoxPrice += outrightTargetPricesMap[secondCombos.spreadList[0].sellLeg % API.n];
                            }
                            else
                            {
                                targetBoxPrice += outrightTargetPricesMap[strategies[stgID].quoteIndex % API.n];
                                targetBoxPrice -= outrightTargetPricesMap[strategies[stgID].correlatedIndices[0] % API.n];
                            }
                        }
                        strategies[stgID].correlatedSpreadTargetPrice = targetBoxPrice;
                        if (strategies[stgID].correlatedIndices.Contains(strategies[stgID].leanIndex))
                            strategies[stgID].boxTargetPrice = targetBoxPrice;
                        else
                            strategies[stgID].boxTargetPrice = 0;
                    }
                }
                if (strategies.ContainsKey(stgID))
                    strategies[stgID].OnProcessMD(vi);
            }
            catch (Exception e)
            {
                API.Log("Exception OnDeal: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void API_OnDeal(KGDeal deal)
        {
            try
            {
                int instrumentIndex = deal.index + API.n * deal.VenueID;
                if (strategies.ContainsKey(deal.stgID))
                {
                    strategies[deal.stgID].OnDeal(deal);
                }
                else if (deal.stgID == -1)
                {
                    API.Log("deal with unknown strategy, working around");
                    for (int i = strategies.Count-1; i >= 0; i--)
                    {
                        var strategy = strategies[i];
                        if (strategy.correlatedIndices.Contains(instrumentIndex) || strategy.crossVenueIndices.Contains(instrumentIndex))
                        {
                            strategy.OnDeal(deal);
                        }
                    }
                }

                if (deal.source == "INTERNAL" || deal.source == "FW")
                    return;

                var instrument = API.GetSecurityNumber(instrumentIndex, deal.VenueID);
                var amount = deal.amount;
                if (instrument.Length > 9)
                    amount *= 2;

                if (volumePerInstrument.ContainsKey(instrument))
                {
                    volumePerInstrument[instrument] += amount;
                }
                else
                {
                    volumePerInstrument.Add(instrument, amount);
                }
            }
            catch (Exception e)
            {
                API.Log("Exception OnDeal: " + e.ToString() + "," + e.StackTrace);
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