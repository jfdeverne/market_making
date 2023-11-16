using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KGClasses;
using StrategyLib;
using System.IO;
using System.Reflection;
using Mapack;
using System.Xml.Linq;

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
            TimeSpan ts = new TimeSpan(0, 0, 0, 0, seconds * 1000);
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
}

public class Box
{
    public int holding;
    public int[] indices = new int[4];
    public double ICM;
    public double targetPrice;
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
        IMatrix V;
        IMatrix VVTInv;
        IMatrix VTVVTInv;
        IMatrix beta;
        int[] boxIndices;
        int[] outrightIndices;
        IMatrix boxTargetPrices; //y
        IMatrix outrightICMs; //xICM
        IMatrix outrightTargetPrices; //x = xICM + VTVVTInv*(y-V*xICM)

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
            try
            {
                int sInd = paramName.IndexOf('.') - 1;
                int strategyIndex = Int32.Parse(paramName.Substring(1, sInd));

                if (strategyIndex == 0)
                {
                    paramName = paramName.Substring(sInd + 2);
                    P.SetValue(paramName, paramVal);
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

                var doc = XDocument.Load(pathQuoter);

                foreach (var quoter in doc.Descendants("Quoter"))
                {
                    QuoterConfig config = new QuoterConfig
                    {
                        width = (double)quoter.Element("width"),
                        size = (int)quoter.Element("size"),
                        leanInstrument = (string)quoter.Element("leanInstrument"),
                        quoteInstrument = (string)quoter.Element("quoteInstrument"),
                        farInstrument = (string)quoter.Element("farInstrument"),
                        icsInstrument = (string)quoter.Element("ics"),
                        asymmetricQuoting = (bool?)quoter.Element("asymmetricQuoting"),
                        defaultBaseSpread = (double?)quoter.Element("defaultBaseSpread"),
                        limitPlusSize = (int?)quoter.Element("limitPlusSize"),
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

                    if (config.farInstrument != null)
                    {
                        var farEl = quoter.Element("farInstrument");
                        if (farEl.Attribute("class").Value == "correlated")
                        {
                            config.correlatedInstruments.Add(config.farInstrument);
                        }
                        else if (farEl.Attribute("class").Value == "crossVenue")
                        {
                            config.crossVenueInstruments.Add(config.farInstrument);
                        }
                    }

                    Strategy s = new Quoter(API, config);
                    strategies[s.stgID] = s;
                }

                string pathBV = Directory.GetCurrentDirectory() + "/bv.xml";
                var docBV = XDocument.Load(pathBV);

                quoteIndices = new List<int>();
                quoteFarIndices = new List<int>();
                leanIndices = new List<int>();
                boxes = new List<Box>();

                int ii = 0;

                foreach (var bv in docBV.Descendants("BV"))
                {
                    BVConfig config = new BVConfig
                    (
                        (string)bv.Element("nearInstrument"),
                        (string)bv.Element("farInstrument"),
                        (string)bv.Element("leanInstrument"),
                        (int)bv.Element("limitPlusSize"),
                        (double)bv.Element("defaultBaseSpread")
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

                    if (API.GetSecurityNumber(s.quoteIndex, 0).Length > 9) //NOT OUTRIGHT
                    {
                        quoteIndices.Add(s.quoteIndex);
                        quoteFarIndices.Add(s.farIndex);
                        leanIndices.Add(s.leanIndex);
                        strategies[s.stgID].linkedBoxIndex = ii; //LINKING THE RELEVANT BOX ENTRY IN THE boxes ARRAYS
                        API.SetBoxTargetPrice(s.stgID, s.boxTargetPrice);
                        ii++;
                    }
                }

                foreach (var bv in docBV.Descendants("LimitBV"))
                {
                    BVConfig config = new BVConfig
                    (
                        (string)bv.Element("nearInstrument"),
                        (string)bv.Element("farInstrument"),
                        (string)bv.Element("leanInstrument"),
                        (int)bv.Element("limitPlusSize"),
                        (double)bv.Element("defaultBaseSpread")
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

                    if (API.GetSecurityNumber(s.quoteIndex, 0).Length > 9) //NOT OUTRIGHT
                    {
                        quoteIndices.Add(s.quoteIndex);
                        quoteFarIndices.Add(s.farIndex);
                        leanIndices.Add(s.leanIndex);
                        strategies[s.stgID].linkedBoxIndex = ii; //LINKING THE RELEVANT BOX ENTRY IN THE boxes ARRAYS
                        API.SetBoxTargetPrice(s.stgID, s.boxTargetPrice);
                        ii++;
                    }
                }

                NBoxes = quoteIndices.Count;
                boxHoldings = new Matrix(NBoxes, 1);
                boxIndices = new int[4]; //leg1, leg2 of 1st calendar spread, then leg1 and leg2 of the 2nd cal spread
                outrightIndices = new int[2 * (NBoxes + 1)];
                V = new Matrix(NBoxes, 2 * (NBoxes + 1));
                beta = new Matrix(2 * (NBoxes + 1), 1);
                boxTargetPrices = new Matrix(NBoxes, 1);
                outrightICMs = new Matrix(2 * (NBoxes + 1), 1);
                outrightTargetPrices = new Matrix(2 * (NBoxes + 1), 1);

                for (int i = 0; i < NBoxes; i++)
                {
                    V[i, i] = 1;
                    V[i, i + 1] = -1;
                    V[i, i + NBoxes + 1] = -1;
                    V[i, i + NBoxes + 2] = 1;
                    Combo combos = API.GetCombos(quoteIndices[i]);
                    Combo leanCombos = API.GetCombos(leanIndices[i]);
                    boxIndices[0] = (combos.spreadList[0].buyLeg) % API.n;
                    boxIndices[1] = (combos.spreadList[0].sellLeg) % API.n;
                    boxIndices[2] = (leanCombos.spreadList[0].buyLeg) % API.n;
                    boxIndices[3] = (leanCombos.spreadList[0].sellLeg) % API.n;
                    boxes.Add(new Box(boxIndices));
                    //WE PRESUME THAT i's sellLeg is (i+1)'s buyLeg:
                    if (i == 0)
                    {
                        outrightIndices[0] = combos.spreadList[0].buyLeg % API.n;
                        outrightIndices[NBoxes + 1] = leanCombos.spreadList[0].buyLeg % API.n;
                    }
                    outrightIndices[i + 1] = combos.spreadList[0].sellLeg % API.n;
                    outrightIndices[i + 1 + NBoxes + 1] = leanCombos.spreadList[0].sellLeg % API.n;
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
                }
                else
                {
                    for (int i = 0; i < NBoxes; i++)
                    {
                        boxIndices = boxes[i].indices;
                        boxes[i].ICM = API.GetImprovedCM(boxIndices[0]) - API.GetImprovedCM(boxIndices[1]) - (API.GetImprovedCM(boxIndices[2]) - API.GetImprovedCM(boxIndices[3]));
                        boxTargetPrices[i, 0] = boxes[i].targetPrice;
                    }
                    for (int i = 0; i < outrightIndices.Length; i++)
                    {
                        outrightICMs[i, 0] = API.GetImprovedCM(outrightIndices[i]);
                    }

                    outrightTargetPrices = outrightICMs.Addition(VTVVTInv.Multiply(boxTargetPrices.Subtraction(V.Multiply(outrightICMs))));
                    if (strategies.ContainsKey(stgID))
                    {
                        double targetBoxPrice = 0;
                        for (int i = 0; i < outrightIndices.Length; i++)
                        {
                            if (strategies[stgID].quoteIndex % API.n == outrightIndices[i])
                                targetBoxPrice += outrightTargetPrices[i, 0];
                            if (strategies[stgID].leanIndex % API.n == outrightIndices[i])
                                targetBoxPrice -= outrightTargetPrices[i, 0];
                        }
                        strategies[stgID].boxTargetPrice = targetBoxPrice;
                    }
                }
                if (strategies.ContainsKey(stgID))
                    strategies[stgID].OnProcessMD(vi);
            }
            catch (Exception)
            {
                API.SendToRemote("API_OnProcessMD:", KGConstants.EVENT_ERROR);
            }
        }

        private void API_OnDeal(KGDeal deal)
        {
            try
            {
                if (strategies.ContainsKey(deal.stgID))
                {
                    strategies[deal.stgID].OnDeal(deal);
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