using Detail;
using StrategyLib;
using System;
using System.Collections.Generic;
using KGClasses;
using System.Reflection;
using System.Xml;

namespace StrategyRunner
{
    public class QuoterConfig
    {
        public double width { get; set; }
        public int size { get; set; }
        public string leanInstrument { get; set; }
        public string quoteInstrument { get; set; }
        public string icsInstrument { get; set; }
        public bool? asymmetricQuoting { get; set; }
        public double? defaultBaseSpread { get; set; }
        public int? limitPlusSize { get; set; }
        public int? nonLeanLimitPlusSize { get; set; }
        public List<string> crossVenueInstruments { get; set; }
        public List<string> correlatedInstruments { get; set; }
    }

    public class Quoter : Strategy
    {
        public QuoterConfig config;

        double theo;

        double maxBidSize;
        double maxAskSize;

        public int icsIndex;

        public int numAllInstruments;
        public int numInstrumentsInVenue;

        VI icsInstrument;
        VI quoteInstrument;

        KGOrder buy;
        KGOrder sell;

        DepthElement prevBid;
        DepthElement prevAsk;

        List<VI> instruments;
        
        public Hedging hedging;

        Throttler.Throttler throttler;

        public static double joinFactor = -1;
        public static double joinFactorAllVenues = -1;
        public static double quoteThrottleSeconds = -1;
        public static int quoteThrottleVolume = -1;

        public static string logLevel = "info";

        public List<VI> crossVenueInstruments;

        public Quoter(API api, QuoterConfig config)
        {
            try
            {
                this.config = config;

                API = api;
                API.Log("-->strategy:" + config.quoteInstrument);

                numAllInstruments = API.N;
                numInstrumentsInVenue = API.n;

                holding = new int[numAllInstruments];
                bids = new DepthElement[numAllInstruments];
                asks = new DepthElement[numAllInstruments];

                for (int i = 0; i < numAllInstruments; i++)
                {
                    holding[i] = 0;
                    bids[i] = new DepthElement(-11, 0);
                    asks[i] = new DepthElement(11111, 0);
                }

                if (config.limitPlusSize.HasValue)
                    limitPlusSize = config.limitPlusSize.Value;
                else
                    limitPlusSize = 200;

                if (config.nonLeanLimitPlusSize.HasValue)
                    nonLeanLimitPlusSize = config.nonLeanLimitPlusSize.Value;
                else
                    nonLeanLimitPlusSize = 50;

                if (config.defaultBaseSpread.HasValue)
                    boxTargetPrice = config.defaultBaseSpread.Value;
                else
                    boxTargetPrice = 0;

                instruments = new List<VI>();

                leanIndex = API.GetSecurityIndex(config.leanInstrument);
                quoteIndex = API.GetSecurityIndex(config.quoteInstrument);

                crossVenueIndices = new List<int>();
                correlatedIndices = new List<int>();
                crossVenueInstruments = new List<VI>();

                foreach (var instrument in config.crossVenueInstruments)
                {
                    int index = API.GetSecurityIndex(instrument);
                    int venue = index / numInstrumentsInVenue;
                    int indexGlobal = index % numInstrumentsInVenue;
                    var vi = new VI(venue, indexGlobal);
                    instruments.Add(vi);
                    crossVenueInstruments.Add(vi);
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

                if (config.icsInstrument != null)
                    icsIndex = API.GetSecurityIndex(config.icsInstrument);
                else
                    icsIndex = -1;

                tickSize = API.GetTickSize(quoteIndex);

                if (icsIndex != -1)
                {
                    int icsVenue = icsIndex / numInstrumentsInVenue;
                    int icsIndexGlobal = icsIndex % numInstrumentsInVenue;
                    icsInstrument = new VI(icsVenue, icsIndexGlobal);
                    instruments.Add(icsInstrument);
                }

                int quoteVenue = quoteIndex / numInstrumentsInVenue;
                int quoteIndexGlobal = quoteIndex % numInstrumentsInVenue;
                quoteInstrument = new VI(quoteVenue, quoteIndexGlobal);

                strategyOrders = new List<KGOrder>();

                buy = new KGOrder();
                strategyOrders.Add(buy);
                sell = new KGOrder();
                strategyOrders.Add(sell);

                double ms = GetEurexThrottleSeconds() * 1000;
                TimeSpan tEurex = new TimeSpan(0, 0, 0, 0, (int)ms);
                eurexThrottler = new Throttler.EurexThrottler(GetEurexThrottleVolume(), tEurex);

                orders = new Orders(this);
                hedging = new Hedging(this);

                double quoteThrottleMs = GetQuoteThrottleSeconds() * 1000;
                TimeSpan t = new TimeSpan(0, 0, 0, 0, (int)quoteThrottleMs);
                throttler = new Throttler.Throttler(GetQuoteThrottleVolume(), t);

                API.Log("-->Start strategy:");
                API.StartStrategy(ref stgID, strategyOrders, instruments, 0, 4);

                API.Log("<--strategy");
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private void LogWarn(string message)
        {
            API.Log(String.Format("STG {0}: {1}", stgID, message));
            API.SendToRemote(String.Format("STG {0}: {1}", stgID, message), KGConstants.EVENT_WARNING);
        }

        private double GetJoinFactor()
        {
            if (joinFactor == -1)
                return P.joinFactor;
            return joinFactor;
        }

        private double GetJoinFactorAllVenues()
        {
            if (joinFactorAllVenues == -1)
                return P.joinFactorAllVenues;
            return joinFactorAllVenues;
        }

        private double GetQuoteThrottleSeconds()
        {
            if (quoteThrottleSeconds == -1)
                return P.quoteThrottleSeconds;
            return quoteThrottleSeconds;
        }

        private int GetQuoteThrottleVolume()
        {
            if (quoteThrottleVolume == -1)
                return P.quoteThrottleVolume;
            return quoteThrottleVolume;
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

        public double GetEurexThrottleSeconds()
        {
            if (eurexThrottleSeconds == -1)
                return P.eurexThrottleSeconds;
            return eurexThrottleSeconds;
        }

        public int GetEurexThrottleVolume()
        {
            if (eurexThrottleVolume == -1)
                return P.eurexThrottleVolume;
            return eurexThrottleVolume;
        }

        public override string GetLogLevel()
        {
            return logLevel;
        }

        public override void OnStatusChanged(int status)
        {
            if (status == 0)
            {
                API.CancelAllOrders(stgID);
            }
        }

        private void CancelStrategy(string reason)
        {
            API.SetStrategyStatus(stgID, 0);
            API.CancelAllOrders(stgID);
            API.SendAlertBeep();
            API.Log(String.Format("CANCEL STG {0}: {1}", stgID, reason));
            API.SendToRemote(String.Format("CANCEL STG {0}: {1}", stgID, reason), KGConstants.EVENT_ERROR);
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

        public void Request2SendKGParams()
        {
            API.Log("-->OnRequest2SendKGParams");
            string line = P.GetParamsStr();
            API.SendToRemote(line, KGConstants.IMPORTANCE_LOW);
            API.Log("<--OnRequest2SendKGParams :" + line);
        }

        public void Request2UpdateParams(string msg)
        {
            try
            {
                string ret = P.SetValues(msg);
                if (ret != "")
                    API.Log(ret, true);
            }
            catch (Exception e)
            {
                API.Log("OnRequest2UpdateParams exception: " + e.ToString() + ", " + e.StackTrace, true);
            }
        }

        public static void UpdateConfig(double newBaseSpreadValue, string instrument)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load("quoter.xml");

            XmlNode bvNode = xmlDoc.SelectSingleNode($"//Quoter[nearInstrument='{instrument}']");

            if (bvNode != null)
            {
                XmlNode baseSpreadNode = bvNode.SelectSingleNode("defaultBaseSpread");

                if (baseSpreadNode != null)
                {
                    baseSpreadNode.InnerText = newBaseSpreadValue.ToString();
                    xmlDoc.Save("quoter.xml");
                }
            }
        }

        public override void OnFlush()
        {
            UpdateConfig(boxTargetPrice, config.quoteInstrument);
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private (double, double) getMinimumLonelinessConstrainedBidAsk(VI instrument)
        {
            double bid = -11;
            double ask = 11111;
            try
            {
                List<DepthElement> bidDepth = API.GetBidDepth(instrument);
                List<DepthElement> askDepth = API.GetAskDepth(instrument);

                int cumulativeBidSize = 0;
                int cumulativeAskSize = 0;

                int levelIndex = 0;
                double lastBid = bid;
                double lastAsk = ask;
                bool reachedSizeTH = false;
                while ((levelIndex < 5) && !reachedSizeTH)
                {
                    lastBid = bidDepth[levelIndex].price;
                    cumulativeBidSize += bidDepth[levelIndex].qty;
                    LogDebug(String.Format("STG {0}: [MM] [QUOTE_LONELINESS] bid={1} cum_size={2} level={3}", stgID, lastBid, cumulativeBidSize, levelIndex));
                    if (levelIndex < 5)
                        levelIndex++;

                    if ((cumulativeBidSize >= config.size * GetJoinFactor()) | ((pricesAreEqual(buy.bid, lastBid)) &(cumulativeBidSize >= config.size * (GetJoinFactor()-2))))
                    {
                        bid = lastBid;
                        reachedSizeTH = true;
                        LogDebug(String.Format("STG {0}: [MM] [QUOTE_LONELINESS] reached size threshold {1}, bid={2}", stgID, config.size * GetJoinFactor(), bid));
                    }
                }

                levelIndex = 0;
                reachedSizeTH = false;
                while ((levelIndex < 5) && !reachedSizeTH)
                {
                    lastAsk = askDepth[levelIndex].price;
                    cumulativeAskSize += askDepth[levelIndex].qty;
                    LogDebug(String.Format("STG {0}: [MM] [QUOTE_LONELINESS] ask={1} cum_size={2} level={3}", stgID, lastAsk, cumulativeAskSize, levelIndex));
                    if (levelIndex < 5)
                        levelIndex++;

                    if ((cumulativeAskSize >= config.size * GetJoinFactor()) | ((pricesAreEqual(sell.ask, lastAsk)) & (cumulativeAskSize >= config.size * (GetJoinFactor() - 2))))
                    {
                        ask = lastAsk;
                        reachedSizeTH = true;
                        LogDebug(String.Format("STG {0}: [MM] [QUOTE_LONELINESS] reached size threshold {1}, ask={2}", stgID, config.size * GetJoinFactor(), ask));
                    }
                }

                if (pricesAreEqual(bid, -11))
                {
                    LogDebug(String.Format("STG {0}: [MM] [QUOTE_LONELINESS] failed to reach size threshold {1} (> cum_size={2})", stgID, config.size * GetJoinFactor(), cumulativeBidSize));
                }
                if (pricesAreEqual(ask, 11111))
                {
                    LogDebug(String.Format("STG {0}: [MM] [QUOTE_LONELINESS] failed to reach size threshold {1} (> cum_size={2})", stgID, config.size * GetJoinFactor(), cumulativeAskSize));
                }
                return (bid, ask);
            }
            catch (Exception e)
            {
                API.Log("getMinimumLonelinessConstrainedBidAsk ERR: " + e.ToString() + "," + e.StackTrace);
                return (bid, ask);
            }
        }

        private (double, double) getMinimumLonelinessConstrainedBidAsk(List<VI> instruments)
        {
            double bid = -11;
            double ask = 11111;
            try
            {
                List<DepthElement> allBids = new List<DepthElement>();
                List<DepthElement> allAsks = new List<DepthElement>();

                foreach (var instrument in instruments)
                {
                    allBids.AddRange(API.GetBidDepth(instrument));
                    allAsks.AddRange(API.GetAskDepth(instrument));
                }

                allBids.Sort((x, y) => y.price.CompareTo(x.price));
                allAsks.Sort((x, y) => x.price.CompareTo(y.price));

                int cumulativeBidSize = 0;
                int cumulativeAskSize = 0;

                foreach (var bidElement in allBids)
                {
                    cumulativeBidSize += bidElement.qty;
                    LogDebug(String.Format("STG {0}: [MM] [CROSS_LONELINESS] bid={1} cum_size={2}", stgID, bidElement.price, cumulativeBidSize));

                    if (cumulativeBidSize >= config.size * GetJoinFactorAllVenues())
                    {
                        LogDebug(String.Format("STG {0}: [MM] [CROSS_LONELINESS] reached size threshold {1} bid={2}", stgID, config.size * GetJoinFactorAllVenues(), bidElement.price));
                        bid = bidElement.price;
                        break;
                    }
                }

                foreach (var askElement in allAsks)
                {
                    cumulativeAskSize += askElement.qty;
                    LogDebug(String.Format("STG {0}: [MM] [CROSS_LONELINESS] ask={1} cum_size={2}", stgID, askElement.price, cumulativeAskSize));

                    if (cumulativeAskSize >= config.size * GetJoinFactorAllVenues())
                    {
                        LogDebug(String.Format("STG {0}: [MM] [CROSS_LONELINESS] reached size threshold {1} ask={2}", stgID, config.size * GetJoinFactorAllVenues(), askElement.price));
                        ask = askElement.price;
                        break;
                    }
                }

                if (pricesAreEqual(bid, -11))
                {
                    LogDebug(String.Format("STG {0}: [MM] [CROSS_LONELINESS] failed to reach size threshold {1} (> cum_size={2})", stgID, config.size * GetJoinFactorAllVenues(), cumulativeBidSize));
                }
                if (pricesAreEqual(ask, 11111))
                {
                    LogDebug(String.Format("STG {0}: [MM] [CROSS_LONELINESS] failed to reach size threshold {1} (> cum_size={2})", stgID, config.size * GetJoinFactorAllVenues(), cumulativeAskSize));
                }

                return (bid, ask);
            }
            catch (Exception e)
            {
                API.Log("getMinimumLonelinessConstrainedBidAsk ERR: " + e.ToString() + "," + e.StackTrace);
                return (bid, ask);
            }
        }


        private void PullQuotes()
        {
            if (orders.orderInUse(buy))
            {
                orders.CancelOrder(buy);
            }

            if (orders.orderInUse(sell))
            {
                orders.CancelOrder(sell);
            }
        }

        private void PullOnSizeDrop(DepthElement bid, ref double maxBidSize, DepthElement ask, ref double maxAskSize)
        {
            if ((bid.qty < maxBidSize - config.size) && (bid.qty < 3 * config.size) && (bid.price - buy.bid < tickSize + 1e-9) && (bid.price < prevBid.price + 1e-9))
            {
                PullQuotes();
                maxBidSize = 0;
                return;
            }

            if ((ask.qty < maxAskSize - config.size) && (ask.qty < 3 * config.size) && (sell.ask - ask.price < tickSize + 1e-9) && (ask.price > prevAsk.price - 1e-9))
            {
                PullQuotes();
                maxAskSize = 0;
                return;
            }
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

        public double GetLeanQuoteSpread()
        {
            if (correlatedIndices.Contains(leanIndex))
            {
                return boxTargetPrice;
            }

            return 0;
        }

        void LogDebug(string message)
        {
            if (logLevel == "debug")
                API.Log(message);
        }

        public override void OnProcessMD(VIT vit)
        {
            try
            {
                orders.OnProcessMD();
                orders.CheckPendingCancels();
                hedging.CheckIOC();

                if (API.GetTimeFromStart() >= 5113251263)
                { }

                VI vi = new VI(vit.v, vit.i);
                int instrumentIndex = vi.i + API.n * vi.v;

                prevBid = bids[leanIndex];
                prevAsk = asks[leanIndex];

                bids[instrumentIndex] = API.GetBid(vi);
                asks[instrumentIndex] = API.GetAsk(vi);

                bids[quoteIndex] = API.GetBid(new VI(quoteIndex / API.n, quoteIndex % API.n));
                asks[quoteIndex] = API.GetAsk(new VI(quoteIndex / API.n, quoteIndex % API.n));

                maxBidSize = bids[leanIndex].qty > maxBidSize ? bids[leanIndex].qty : maxBidSize;
                maxAskSize = asks[leanIndex].qty > maxAskSize ? asks[leanIndex].qty : maxAskSize;

                if (!API.PassedTradeStart())
                {
                    return;
                }

                if (API.GetStrategyStatus(stgID) == 0 || systemTradingMode == 'C')
                {
                    return;
                }

                theo = API.GetImprovedCM(leanIndex);

                bool quote = !pricesAreEqual(theo, -11);
                double quoteTheo = theo + GetLeanQuoteSpread();

                var (quoteBid, quoteAsk) = getMinimumLonelinessConstrainedBidAsk(crossVenueInstruments);
                var (otherVenueBid, otherVenueAsk) = getMinimumLonelinessConstrainedBidAsk(quoteInstrument);
                quoteBid = Math.Round((Math.Min(quoteBid, otherVenueBid) - (tickSize / 2 - 1e-9)) / tickSize) * tickSize;
                quoteAsk = Math.Round((Math.Max(quoteAsk, otherVenueAsk) + (tickSize / 2 - 1e-9)) / tickSize) * tickSize;

                if (pricesAreEqual(quoteBid, -11) || pricesAreEqual(quoteAsk, 11111))
                {
                    if (instrumentIndex == quoteIndex && bids[instrumentIndex].qty >= 25 && asks[instrumentIndex].qty >= 25)
                        LogDebug("not quoting despite volume on quote");

                    quote = false;
                    LogDebug(String.Format("STG {0}: [MM] unable to calculate valid quote, bid={1} ask={2} time={3}", stgID, quoteBid, quoteAsk, API.GetTimeFromStart()));
                }

                if (quoteAsk - quoteBid - config.width > 1e-7) //UNABLE TO FULFILL LONELINESS CONDS
                {
                    quote = false;
                    LogDebug(String.Format("STG {0}: [MM] spread too wide, bid={1} ask={2} config_width={3} time={4}", stgID, quoteBid, quoteAsk, config.width, API.GetTimeFromStart()));
                }
                else
                {
                    if (quoteAsk - quoteBid - config.width < -1e-7) //LONELINESS CONSTRAINT LEAVES DEGREE OF FREEDOM, TRY TO IMPROVE THE QUOTE
                    {
                        while (quoteAsk - quoteBid - config.width < -1e-7)
                        {
                            LogDebug("improving quote");
                            if (leanIndex % API.n == quoteIndex % API.n)
                            {
                                if ((bids[leanIndex].qty < P.joinFactorAllVenues * config.size + 1) & pricesAreEqual(bids[leanIndex].price, quoteBid) & (quoteAsk - quoteBid - config.width < -1e-7))
                                    quoteBid -= tickSize;
                                if ((asks[leanIndex].qty < P.joinFactorAllVenues * config.size + 1) & pricesAreEqual(asks[leanIndex].price, quoteAsk) & (quoteAsk - quoteBid - config.width < -1e-7))
                                    quoteAsk += tickSize;
                            }

                            if ((quoteTheo >= (quoteAsk + quoteBid) / 2 + tickSize / 4) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteAsk += tickSize;
                            if ((quoteTheo <= (quoteBid + quoteAsk) / 2 - tickSize / 4) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteBid -= tickSize;
                            if ((holding[quoteIndex] > config.size - 1) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteBid -= tickSize;
                            if ((holding[quoteIndex] < -config.size + 1) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteAsk += tickSize;
                            if ((quoteTheo >= (quoteAsk + quoteBid) / 2) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteAsk += tickSize;
                            if ((quoteTheo <= (quoteBid + quoteAsk) / 2) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteBid -= tickSize;
                            if ((holding[quoteIndex] > 0) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteBid -= tickSize;
                            if ((holding[quoteIndex] < 0) & (quoteAsk - quoteBid - config.width < -1e-7))
                                quoteAsk += tickSize;
                        }

                        if (quoteBid < -10 || quoteAsk > 1000)
                        {
                            LogDebug(String.Format("STG {0}: [MM] invalid quote after adjustment, bid={1} ask={2} time={3}", stgID, quoteBid, quoteAsk, API.GetTimeFromStart()));
                            quote = false;
                        }

                        if (quoteAsk - quoteBid > config.width + 1e-9)
                        {
                            LogDebug(String.Format("STG {0}: [MM] spread too wide after adjustment, bid={1} ask={2} config_width={3} time={4}", stgID, quoteBid, quoteAsk, config.width, API.GetTimeFromStart()));
                            quote = false;
                        }
                    }
                }

                if ((orders.orderInUse(buy) || orders.orderInUse(sell)) && !orders.orderInTransientState(buy) && !orders.orderInTransientState(sell))
                {
                    PullOnSizeDrop(bids[leanIndex], ref maxBidSize, asks[leanIndex], ref maxAskSize);

                    if (!quote)
                    {
                        if (orders.orderInUse(buy) && !orders.orderInTransientState(buy))
                        {
                            LogDebug(String.Format("STG {0}: [MM] quoting req not met, cancel buy order {1} time={2}", stgID, buy.internalOrderNumber, API.GetTimeFromStart()));
                            orders.CancelOrder(buy);
                        }

                        if (orders.orderInUse(sell) && !orders.orderInTransientState(sell))
                        {
                            LogDebug(String.Format("STG {0}: [MM] quoting req not met, cancel sell order {1} time={2}", stgID, sell.internalOrderNumber, API.GetTimeFromStart()));
                            orders.CancelOrder(sell);
                        }

                        return;
                    }

                    if (!pricesAreEqual(prevBid.price, bids[leanIndex].price))
                    {
                        if (orders.orderInUse(buy))
                        {
                            if (!orders.orderInTransientState(buy))
                            {
                                LogDebug(String.Format("STG {0}: [MM] lean move, cancel buy order {1} time={2}", stgID, buy.internalOrderNumber, API.GetTimeFromStart()));
                                orders.CancelOrder(buy);
                            }
                        }
                    }
                    if (!pricesAreEqual(prevAsk.price, asks[leanIndex].price))
                    {
                        if (orders.orderInUse(sell))
                        {
                            if (!orders.orderInTransientState(sell))
                            {
                                LogDebug(String.Format("STG {0}: [MM] lean move, cancel sell order {1} time={2}", stgID, sell.internalOrderNumber, API.GetTimeFromStart()));
                                orders.CancelOrder(sell);
                            }
                        }
                    }
                }

                if (!quote)
                {
                    return;
                }

                if ((quoteBid >= asks[quoteIndex].price) & (asks[quoteIndex].qty > 0))
                {
                    LogWarn(String.Format("quote_bid={0} >= best_offer={1}", quoteBid, asks[quoteIndex].price));
                    quoteBid = asks[quoteIndex].price - tickSize;
                }

                if ((quoteAsk <= bids[quoteIndex].price) & (bids[quoteIndex].qty > 0))
                {
                    LogWarn(String.Format("quote_ask={0} <= best_bid={1}", quoteAsk, bids[quoteIndex].price));
                    quoteAsk = bids[quoteIndex].price + tickSize;
                }

                int netHolding = GetNetPosition();

                if (netHolding != 0)
                {
                    hedging.Hedge();

                    if (orders.orderInUse(buy) && !pricesAreEqual(buy.bid, quoteBid))
                    {
                        LogDebug(String.Format("STG {0}: [MM] hedging position={1}, buy order in use, buy_bid={2} != quote_bid={3}, cancelling. time={4}", stgID, netHolding, buy.bid, quoteBid, API.GetTimeFromStart()));
                        orders.CancelOrder(buy);
                    }

                    if (orders.orderInUse(sell) && !pricesAreEqual(sell.ask, quoteAsk))
                    {
                        LogDebug(String.Format("STG {0}: [MM] hedging position={1}, sell order in use, sell_ask={2} != quote_ask={3}, cancelling. time={4}", stgID, netHolding, sell.ask, quoteAsk, API.GetTimeFromStart()));
                        orders.CancelOrder(sell);
                    }

                    return;
                }

                hedging.CancelAllHedgeOrders();

                int quoteSizeBid = config.size;
                int quoteSizeAsk = config.size;

                if (config.asymmetricQuoting.HasValue && config.asymmetricQuoting.Value && P.enableAsymmetricQuoting)
                {
                    double theo = API.GetImprovedCM(quoteIndex);
                    double mid = (quoteBid + quoteAsk) / 2;
                    if ((buy.bidSize == quoteSizeBid * 2 + 1) & (theo > mid - tickSize*0.3))
                    {
                        quoteSizeBid = buy.bidSize;
                    }
                    else if ((sell.askSize == quoteSizeAsk * 2 + 1) & (theo < mid + tickSize * 0.3))
                    {
                        quoteSizeAsk = sell.askSize;
                    }
                    else if (theo > mid)
                    {
                        quoteSizeBid = quoteSizeBid * 2 + 1;
                        LogDebug(String.Format("STG {0}: [MM] asymmetric quoting: theo={1} > mid={2}, quote_size_bid={3} quote_size_ask={4} time={5}", stgID, theo, mid, quoteSizeBid, quoteSizeAsk, API.GetTimeFromStart()));
                    }
                    else
                    {
                        quoteSizeAsk = quoteSizeAsk * 2 + 1;
                        LogDebug(String.Format("STG {0}: [MM] asymmetric quoting: theo={1} <= mid={2}, quote_size_bid={3} quote_size_ask={4} time={5}", stgID, theo, mid, quoteSizeBid, quoteSizeAsk, API.GetTimeFromStart()));
                    }
                }

                if (!orders.orderInTransientState(buy) && quote && (!pricesAreEqual(buy.bid, quoteBid) || buy.bidSize != quoteSizeBid))
                {
                    LogDebug(String.Format("STG {0}: [MM] posting buy order instrument={1} bid={2} bid_size={3} time={4}", stgID, quoteIndex, quoteBid, quoteSizeBid, API.GetTimeFromStart()));
                    orders.SendOrder(buy, quoteIndex, Side.BUY, quoteBid, quoteSizeBid, "MM", true);
                }
                else if (quote)
                {
                    LogDebug(String.Format("STG {0}: [MM] NOT posting buy order instrument={1} bid={2} order_bid={3} bid_size={4} order_bid_size={5} order_status={6} order_internal={7} order_no={8} time={9}", stgID, quoteIndex, quoteBid, buy.bid, quoteSizeBid, buy.bidSize, buy.orderStatus, buy.internalOrderNumber, buy.orderNumber, API.GetTimeFromStart()));
                }

                if (!orders.orderInTransientState(sell) && quote && (!pricesAreEqual(sell.ask, quoteAsk) || sell.askSize != quoteSizeAsk))
                {
                    LogDebug(String.Format("STG {0}: [MM] posting sell order instrument={1} ask={2} ask_size={3} time={4}", stgID, quoteIndex, quoteAsk, quoteSizeAsk, API.GetTimeFromStart()));
                    orders.SendOrder(sell, quoteIndex, Side.SELL, quoteAsk, quoteSizeAsk, "MM", true);
                }
                else if (quote)
                {
                    LogDebug(String.Format("STG {0}: [MM] NOT posting sell order instrument={1} ask={2} order_ask={3} ask_size={4} order_ask_size={5} order_status={6} order_internal={7} order_no={8} time={9}", stgID, quoteIndex, quoteAsk, sell.ask, quoteSizeAsk, sell.askSize, sell.orderStatus, sell.internalOrderNumber, sell.orderNumber, API.GetTimeFromStart()));
                }
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
            throttler.updateTimespan(GetQuoteThrottleSeconds());
            throttler.updateMaxVolume(GetQuoteThrottleVolume());
            eurexThrottler.updateMaxVolume(GetEurexThrottleVolume());
            eurexThrottler.updateTimespan(GetEurexThrottleSeconds());
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
                if (deal.source == "FW")
                    return;

                if (deal.source == "MM")
                    orders.CancelOrder(deal.internalOrderNumber);

                int amount = deal.isBuy ? deal.amount : -deal.amount;

                int instrumentIndex = deal.index + API.n * deal.VenueID;

                holding[instrumentIndex] += amount;

                hedging.Hedge();
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
            foreach (FieldInfo field in typeof(Quoter).GetFields())
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
