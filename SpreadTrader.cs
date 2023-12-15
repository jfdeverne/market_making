using System;
using System.Collections.Generic;
using KGClasses;
using StrategyLib;
using System.Timers;
using Detail;
using System.Reflection;
using System.Xml;
using System.Text;
using System.IO;

namespace StrategyRunner
{
    public class SpreadTrader : Strategy
    {
        private BVConfig config;

        public int numAllInstruments;
        public int numInstrumentsInVenue;

        KGOrder buy;
        KGOrder sell;

        KGOrder limitBuy;
        KGOrder limitSell;

        List<VI> instruments;
        Dictionary<int /*orderId*/, int /*amount*/> pendingOrders;
        Dictionary<int /*orderId*/, int /*volume*/> pendingResubmissions;

        Throttler.Throttler bvThrottler;

        public int[] marketVolumeTraded;

        Timer hedgeTimeout;
        bool shouldHedge = false;

        Hedging hedging;
        Base baseSpreads;

        StringBuilder csvContent;

        public static double bvThrottleSeconds = -1;
        public static int bvThrottleVolume = -1;
        public static double creditOffset = -1;
        public static int maxCrossVolume = -1;
        public static int maxOutrights = -1;
        public static int maxPosNear = -1;
        public static int minPosNear = -1;
        public static int maxPosFar = -1;
        public static int minPosFar = -1;
        public static double bvTimeoutSeconds = -1;

        public static string logLevel = "info";

        double quoteEntryPrice = 0.0;

        public SpreadTrader(API api, BVConfig config)
        {
            try
            {
                this.config = config;

                API = api;
                API.Log("-->strategy:" + config.nearInstrument);

                systemTradingMode = 'C';

                numAllInstruments = API.N;
                numInstrumentsInVenue = API.n;

                holding = new int[numAllInstruments];
                bids = new DepthElement[numAllInstruments];
                asks = new DepthElement[numAllInstruments];
                marketVolumeTraded = new int[numAllInstruments];

                for (int i = 0; i < numAllInstruments; i++)
                {
                    holding[i] = 0;
                    bids[i] = new DepthElement(-11, 0);
                    asks[i] = new DepthElement(11111, 0);
                    marketVolumeTraded[i] = 0;
                }

                tickSize = API.GetTickSize(quoteIndex);

                if (config.limitPlusSize.HasValue)
                    limitPlusSize = config.limitPlusSize.Value;
                else
                    limitPlusSize = 300;

                if (config.nonLeanLimitPlusSize.HasValue)
                    nonLeanLimitPlusSize = config.nonLeanLimitPlusSize.Value;
                else
                    nonLeanLimitPlusSize = 50;

                if (config.defaultBaseSpread.HasValue)
                    boxTargetPrice = config.defaultBaseSpread.Value;
                else
                    boxTargetPrice = 0;

                instruments = new List<VI>();
                pendingOrders = new Dictionary<int, int>();

                quoteIndex = API.GetSecurityIndex(config.nearInstrument);
                farIndex = API.GetSecurityIndex(config.farInstrument);
                leanIndex = API.GetSecurityIndex(config.leanInstrument);

                int nearVenue = quoteIndex / numInstrumentsInVenue;
                int nearIndexGlobal = quoteIndex % numInstrumentsInVenue;

                int farVenue = farIndex / numInstrumentsInVenue;
                int farIndexGlobal = farIndex % numInstrumentsInVenue;

                int leanVenue = leanIndex / numInstrumentsInVenue;
                int leanIndexGlobal = leanIndex % numInstrumentsInVenue;

                crossVenueIndices = new List<int>();
                correlatedIndices = new List<int>();
                foreach (var instrument in config.crossVenueInstruments)
                {
                    int index = API.GetSecurityIndex(instrument);
                    int venue = index / numInstrumentsInVenue;
                    int indexGlobal = index % numInstrumentsInVenue;
                    instruments.Add(new VI(venue, indexGlobal));
                    crossVenueIndices.Add(index);
                }
                foreach (var instrument in config.correlatedInstruments)
                {
                    int index = API.GetSecurityIndex(instrument);
                    int venue = index / numInstrumentsInVenue;
                    int indexGlobal = index % numInstrumentsInVenue;
                    instruments.Add(new VI(venue, indexGlobal));
                    correlatedIndices.Add(index);
                }

                strategyOrders = new List<KGOrder>();

                buy = new KGOrder();
                strategyOrders.Add(buy);
                sell = new KGOrder();
                strategyOrders.Add(sell);

                limitBuy = new KGOrder();
                strategyOrders.Add(limitBuy);
                limitSell = new KGOrder();
                strategyOrders.Add(limitSell);

                double bvThrottleMs = GetBvThrottleSeconds() * 1000;
                TimeSpan tBv = new TimeSpan(0, 0, 0, 0, (int)bvThrottleMs);
                bvThrottler = new Throttler.Throttler(GetBvThrottleVolume(), tBv);

                hedgeTimeout = new Timer();
                hedgeTimeout.Elapsed += OnHedgeTimeout;
                hedgeTimeout.AutoReset = false;

                pendingResubmissions = new Dictionary<int, int>();

                double ms = GetEurexThrottleSeconds() * 1000;
                TimeSpan t = new TimeSpan(0, 0, 0, 0, (int)ms);
                eurexThrottler = new Throttler.EurexThrottler(GetEurexThrottleVolume(), t);

                orders = new Orders(this);
                hedging = new Hedging(this);
                baseSpreads = new Base(this);

                csvContent = new StringBuilder();

                csvContent.AppendLine("trade,quote_bid,quote_ask,quote_bid_size,quote_ask_size,lean_bid,lean_ask,lean_bid_size,lean_ask_size,quote_theo,lean_theo,box_target_price");

                API.Log("-->Start strategy:");
                API.StartStrategy(ref stgID, strategyOrders, instruments, 0, 5);

                API.Log("<--strategy");
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private double GetBvThrottleSeconds()
        {
            if (bvThrottleSeconds == -1)
                return P.bvThrottleSeconds;
            return bvThrottleSeconds;
        }

        private int GetBvThrottleVolume()
        {
            if (bvThrottleVolume == -1)
                return P.bvThrottleVolume;
            return bvThrottleVolume;
        }

        public double GetCreditOffset()
        {
            if (creditOffset == -1)
                return P.creditOffset;
            return creditOffset;
        }

        private double GetBvTimeoutSeconds()
        {
            if (bvTimeoutSeconds == -1)
                return P.bvTimeoutSeconds;
            return bvTimeoutSeconds;
        }

        public override double GetMaxLossMarketHedge()
        {
            if (maxLossMarketHedge == -1)
                return P.maxLossMarketHedge;
            return maxLossMarketHedge;
        }
        public override double GetMaxLossLimitHedge()
        {
            if (maxLossMarketHedge == -1)
                return P.maxLossLimitHedge;
            return maxLossLimitHedge;
        }

        private double GetEurexThrottleSeconds()
        {
            if (eurexThrottleSeconds == -1)
                return P.eurexThrottleSeconds;
            return eurexThrottleSeconds;
        }

        private int GetEurexThrottleVolume()
        {
            if (eurexThrottleVolume == -1)
                return P.eurexThrottleVolume;
            return eurexThrottleVolume;
        }

        public override int GetEusCandidatePosition()
        {
            throw new NotImplementedException();
        }

        public override int GetNetPosition()
        {
            int netHolding = 0;

            foreach (var instrument in correlatedIndices)
            {
                netHolding += holding[instrument];
            }

            foreach (var instrument in crossVenueIndices)
            {
                netHolding += holding[instrument];
            }

            return netHolding;
        }

        private void HedgeLeftovers()
        {
            foreach (var deal in pendingOrders)
            {
                orders.CancelOrder(deal.Key);
            }

            //TODO: this can be triggered by timeout or price running away.
            //At this point we need to cancel all baseSpreads hedger orders and make sure baseSpreads.Hedge() is no longer called.

            //hedging.Hedge();
            //shouldHedge = true;
        }

        public override string GetLogLevel()
        {
            return logLevel;
        }

        private void Log(string message)
        {
            API.Log(String.Format("STG {0}: {1}", stgID, message));
            API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        public override void OnStatusChanged(int status)
        {
            if (status == 0)
            {
                API.CancelAllOrders(stgID);
            }
        }

        public override void OnSystemTradingMode(ref char c)
        {
            systemTradingMode = c;

            if (c == 'C')
            {
                if (API.GetStrategyStatus(stgID) != 0)
                {
                    API.SetStrategyStatus(stgID, 0);
                    API.CancelAllOrders(stgID);
                }
            }
        }

        public void UpdateConfig(double newBaseSpreadValue, string instrument)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load("bv.xml");

            XmlNode bvNode = xmlDoc.SelectSingleNode($"//LimitBV[nearInstrument='{instrument}']");

            if (bvNode != null)
            {
                XmlNode baseSpreadNode = bvNode.SelectSingleNode("defaultBaseSpread");

                if (baseSpreadNode != null)
                {
                    baseSpreadNode.InnerText = newBaseSpreadValue.ToString();
                    xmlDoc.Save("bv.xml");
                }
            }
        }

        public override void OnFlush()
        {
            UpdateConfig(boxTargetPrice, config.nearInstrument);

            File.WriteAllText("spread_trader.csv", csvContent.ToString());
        }

        private void CancelStrategy(string reason)
        {
            API.SetStrategyStatus(stgID, 0);
            API.CancelAllOrders(stgID);
            API.SendAlertBeep();
            API.Log(String.Format("CANCEL STG {0}: {1}", stgID, reason));
            API.SendToRemote(String.Format("CANCEL STG {0}: {1}", stgID, reason), KGConstants.EVENT_ERROR);
        }

        private void OnHedgeTimeout(object sender, ElapsedEventArgs e)
        {
            try
            {
                HedgeLeftovers();
            }
            catch (Exception ex)
            {
                API.Log("ERR: " + ex.ToString() + "," + ex.StackTrace);
            }
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private void Spread()
        {
            if (false)
            {
                //TODO: use pricing to determine if completing the base spread is unlikely and we should send position to regular hedging (so including reverting the original at market trade in quoted at one of the venues)
            }

            if (GetNetPosition() != 0)
            {
                baseSpreads.Hedge();
                return;
            }

            double bid = bids[quoteIndex].price - asks[leanIndex].price;
            double ask = asks[quoteIndex].price - bids[leanIndex].price;
            
            double bidSize = asks[leanIndex].qty;
            double askSize = bids[leanIndex].qty;

            int quantity = 10;

            //TODO: maybe there is room for an eusCandidatePosition type of variable here as the moment we send an order there is nothing to hedge yet and we want to send the limit order simultaneously with the market order (?)

            if (bid >= boxTargetPrice && bidSize > 1000)
            {
                orders.SendOrder(sell, quoteIndex, Side.SELL, bids[quoteIndex].price, quantity, "SPREAD_TRADER");
                quoteEntryPrice = bids[quoteIndex].price;
                //baseSpreads.Hedge();

                csvContent.AppendLine($"quote,{bids[quoteIndex].price},{asks[quoteIndex].price},{bids[quoteIndex].qty},{asks[quoteIndex].qty}," +
                    $"{bids[leanIndex].price},{asks[leanIndex].price},{bids[leanIndex].qty},{asks[leanIndex].qty}," +
                    $"{API.GetImprovedCM(quoteIndex)},{API.GetImprovedCM(leanIndex)},{boxTargetPrice},-11");
            }
            else if (ask <= boxTargetPrice && askSize > 1000)
            {
                orders.SendOrder(buy, quoteIndex, Side.BUY, asks[quoteIndex].price, quantity, "SPREAD_TRADER");
                quoteEntryPrice = asks[quoteIndex].price;
                //baseSpreads.Hedge();

                csvContent.AppendLine($"quote,{bids[quoteIndex].price},{asks[quoteIndex].price},{bids[quoteIndex].qty},{asks[quoteIndex].qty}," +
                    $"{bids[leanIndex].price},{asks[leanIndex].price},{bids[leanIndex].qty},{asks[leanIndex].qty}," +
                    $"{API.GetImprovedCM(quoteIndex)},{API.GetImprovedCM(leanIndex)},{boxTargetPrice},-11");
            }
        }

        public override void OnProcessMD(VIT vit)
        {
            try
            {
                VI vi = new VI(vit.v, vit.i);
                int instrumentIndex = vi.i + API.n * vi.v;

                bids[instrumentIndex] = API.GetBid(vi);
                asks[instrumentIndex] = API.GetAsk(vi);

                if (!API.PassedTradeStart())
                {
                    return;
                }

                if (API.GetStrategyStatus(stgID) == 0 || systemTradingMode == 'C')
                {
                    return;
                }

                orders.OnProcessMD();
                orders.CheckPendingCancels();
                hedging.CheckIOC();

                Spread();
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        public override void OnParamsUpdate(string paramName, string paramValue)
        {
            SetValue(paramName, paramValue);
        }

        public override void OnGlobalParamsUpdate()
        {
            bvThrottler.updateMaxVolume(GetBvThrottleVolume());
            bvThrottler.updateTimespan(GetBvThrottleSeconds());

            eurexThrottler.updateMaxVolume(GetEurexThrottleVolume());
            eurexThrottler.updateTimespan(GetEurexThrottleSeconds());

            double bvTimeoutSeconds = GetBvTimeoutSeconds();
            if (bvTimeoutSeconds > 0)
            {
                hedgeTimeout.Interval = bvTimeoutSeconds * 1000;
            }
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
                if (deal.source == "FW")
                    return;

                if (deal.source == "BASE_SPREADS")
                {
                    csvContent.AppendLine($"lean,{bids[quoteIndex].price},{asks[quoteIndex].price},{bids[quoteIndex].qty},{asks[quoteIndex].qty}," +
                    $"{bids[leanIndex].price},{asks[leanIndex].price},{bids[leanIndex].qty},{asks[leanIndex].qty}," +
                    $"{API.GetImprovedCM(quoteIndex)},{API.GetImprovedCM(leanIndex)},{boxTargetPrice},{quoteEntryPrice - deal.price}");
                }

                int amount = deal.isBuy ? deal.amount : -deal.amount;
                int instrumentIndex = deal.index + API.n * deal.VenueID;
                holding[instrumentIndex] += amount;
            }
            catch (Exception e)
            {
                CancelStrategy(String.Format("OnDeal exception: {1}", stgID, e.ToString()));
            }
        }

        public override void OnPurgedOrder(KGOrder ord)
        {
            CancelStrategy(String.Format("order {0} purged by exchange", ord.internalOrderNumber));
        }

        public override void OnOrder(KGOrder ord)
        {
            API.Log(String.Format("OnOrder: int={0} status={1} sec={2} stg={3} ask_size={4} bid_size={5}", ord.internalOrderNumber, ord.orderStatus, ord.securityNumber, ord.stgID, ord.askSize, ord.bidSize));
        }

        public static (bool, string) SetValue(string paramName, string paramValue)
        {
            string ret = "";
            bool found = false;
            bool valueChanged = false;
            foreach (FieldInfo field in typeof(BV).GetFields())
            {
                if (field.Name != paramName)
                    continue;
                else
                {
                    found = true;
                    if (field.FieldType == typeof(int))
                    {
                        int val = Int32.Parse(paramValue);
                        valueChanged = val != (int)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    else if (field.FieldType == typeof(string))
                    {
                        valueChanged = paramValue != (string)field.GetValue(null);
                        field.SetValue(null, paramValue);
                    }
                    else if (field.FieldType == typeof(double))
                    {
                        double val = Double.Parse(paramValue);
                        valueChanged = val != (double)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    else if (field.FieldType == typeof(bool))
                    {
                        if (paramValue == "true")
                        {
                            valueChanged = !(bool)field.GetValue(null);
                            field.SetValue(null, true);
                        }
                        else
                        {
                            valueChanged = (bool)field.GetValue(null);
                            field.SetValue(null, false);
                        }
                    }
                    else if (field.FieldType == typeof(long))
                    {
                        long val = long.Parse(paramValue);
                        valueChanged = val != (long)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    break;
                }
            }

            return (valueChanged, ret);
        }
    }
}