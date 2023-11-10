using Detail;
using StrategyLib;
using System;
using System.Collections.Generic;
using KGClasses;
using System.Reflection;

namespace StrategyRunner
{
    public class QuoterConfig
    {
        public double width { get; set; }
        public int size { get; set; }
        public string leanInstrument { get; set; }
        public string quoteInstrument { get; set; }
        public string farInstrument { get; set; }
        public string icsInstrument { get; set; }
        public bool? asymmetricQuoting { get; set; }
        public double? defaultBaseSpread { get; set; }
        public int? limitPlusSize { get; set; }
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

        VI leanInstrument;
        VI quoteInstrument;
        VI farInstrument;
        VI icsInstrument;

        KGOrder buy;
        KGOrder sell;

        KGOrder buyFar;
        KGOrder sellFar;

        DepthElement prevBid;
        DepthElement prevAsk;

        List<VI> instruments;
        
        public Hedging hedging;

        Throttler.Throttler throttler;

        public static double joinFactor = -1;
        public static double joinFactorAllVenues = -1;
        public static int quoteThrottleSeconds = -1;
        public static int quoteThrottleVolume = -1;

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

                //boxTargetPrice = config.defaultBaseSpread;

                instruments = new List<VI>();

                leanIndex = API.GetSecurityIndex(config.leanInstrument);
                quoteIndex = API.GetSecurityIndex(config.quoteInstrument);

                if (config.farInstrument != null)
                    farIndex = API.GetSecurityIndex(config.farInstrument);
                else
                    farIndex = -1;

                if (config.icsInstrument != null)
                    icsIndex = API.GetSecurityIndex(config.icsInstrument);
                else
                    icsIndex = -1;

                tickSize = API.GetTickSize(quoteIndex);

                int leanVenue = leanIndex / numInstrumentsInVenue;
                int leanIndexGlobal = leanIndex % numInstrumentsInVenue;

                int quoteVenue = quoteIndex / numInstrumentsInVenue;
                int quoteIndexGlobal = quoteIndex % numInstrumentsInVenue;

                leanInstrument = new VI(leanVenue, leanIndexGlobal);
                quoteInstrument = new VI(quoteVenue, quoteIndexGlobal);

                instruments.Add(leanInstrument);
                instruments.Add(quoteInstrument);

                if (icsIndex != -1)
                {
                    int icsVenue = icsIndex / numInstrumentsInVenue;
                    int icsIndexGlobal = icsIndex % numInstrumentsInVenue;
                    icsInstrument = new VI(icsVenue, icsIndexGlobal);
                    instruments.Add(icsInstrument);
                }

                if (farIndex != -1)
                {
                    int farVenue = farIndex / numInstrumentsInVenue;
                    int farIndexGlobal = farIndex % numInstrumentsInVenue;
                    farInstrument = new VI(farVenue, farIndexGlobal);
                    instruments.Add(farInstrument);
                }

                strategyOrders = new List<KGOrder>();

                buy = new KGOrder();
                strategyOrders.Add(buy);
                sell = new KGOrder();
                strategyOrders.Add(sell);
                buyFar = new KGOrder();
                strategyOrders.Add(buyFar);
                sellFar = new KGOrder();
                strategyOrders.Add(sellFar);

                orders = new Orders(this);
                hedging = new Hedging(this);

                TimeSpan t = new TimeSpan(0, 0, 0, 0, GetQuoteThrottleSeconds() * 1000);
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

        private int GetQuoteThrottleSeconds()
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

        private static double RoundToNearestTick(double price, double tick)
        {
            double numTicks = price / tick;
            int roundedTicks = (int)Math.Round(numTicks, MidpointRounding.AwayFromZero);
            double roundedPrice = roundedTicks * tick;

            return roundedPrice;
        }

        private (double bid, double ask) GetBidAsk(double theoreticalPrice, double tickSize, double width, int index)
        {
            double bid = RoundToNearestTick(theoreticalPrice - width, tickSize);
            double ask = RoundToNearestTick(theoreticalPrice + width, tickSize);

            //TODO: replace with the new GetDirection() logic (need to add magnitude)
            if (holding[index] > 0)
            {
                bid -= tickSize;
                ask -= tickSize;
            }
            else if (holding[index] < 0)
            {
                bid += tickSize;
                ask += tickSize;
            }

            while ((ask - bid) > 2 * width + 1e-9)
            {
                if ((ask - theoreticalPrice) > (theoreticalPrice - bid))
                {
                    ask -= tickSize;
                }
                else
                {
                    bid += tickSize;
                }
            }

            if (bid < -10 || ask > 100)
            {
                return (0, 0);
            }

            return (bid, ask);
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

        public override void OnFlush()
        {

        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private (double, double) getMinimumLonelinessConstrainedBidAsk(VI instrument, VI farInstrument)
        {
            double bid = -11;
            double ask = 11111;
            try
            {
                List<DepthElement> bidDepth = API.GetBidDepth(instrument);
                List<DepthElement> askDepth = API.GetAskDepth(instrument);
                List<DepthElement> bidDepthFar = API.GetBidDepth(farInstrument);
                List<DepthElement> askDepthFar = API.GetAskDepth(farInstrument);

                int cumulativeBidSize = 0;
                int cumulativeAskSize = 0;
                int cumulativeBidSizeFar = 0;
                int cumulativeAskSizeFar = 0;

                int levelIndex = 0;
                int levelIndexFar = 0;
                double lastBid = bid;
                double lastAsk = ask;
                bool reachedSizeTH = false;
                while (((levelIndex < 5) & (levelIndexFar < 5)) & !reachedSizeTH)
                {
                    if (bidDepth[levelIndex].price > bidDepthFar[levelIndexFar].price)
                    {
                        lastBid = bidDepth[levelIndex].price;
                        cumulativeBidSize += bidDepth[levelIndex].qty;
                        if (levelIndex < 5)
                            levelIndex++;
                    }
                    else if (bidDepth[levelIndex].price < bidDepthFar[levelIndexFar].price)
                    {
                        lastBid = bidDepthFar[levelIndexFar].price;
                        cumulativeBidSizeFar += bidDepthFar[levelIndexFar].qty;
                        if (levelIndexFar < 5)
                            levelIndexFar++;
                    }
                    else //==
                    {
                        lastBid = bidDepth[levelIndex].price;
                        cumulativeBidSize += bidDepth[levelIndex].qty;
                        if (levelIndex < 5)
                            levelIndex++;
                        cumulativeBidSizeFar += bidDepthFar[levelIndexFar].qty; ;
                        if (levelIndexFar < 5)
                            levelIndexFar++;
                    }
                    if ((cumulativeBidSize >= config.size * GetJoinFactor()) | (cumulativeBidSize + cumulativeBidSizeFar >= config.size * GetJoinFactorAllVenues()))
                    {
                        bid = lastBid;
                        reachedSizeTH = true;
                    }
                }

                levelIndex = 0;
                levelIndexFar = 0;
                reachedSizeTH = false;
                while (((levelIndex < 5) & (levelIndexFar < 5)) & !reachedSizeTH)
                {
                    if (askDepth[levelIndex].price < askDepthFar[levelIndexFar].price)
                    {
                        lastAsk = askDepth[levelIndex].price;
                        cumulativeAskSize += askDepth[levelIndex].qty;
                        if (levelIndex < 5)
                            levelIndex++;
                    }
                    else if (askDepth[levelIndex].price > askDepthFar[levelIndexFar].price)
                    {
                        lastAsk = askDepthFar[levelIndexFar].price;
                        cumulativeAskSizeFar += askDepthFar[levelIndexFar].qty;
                        if (levelIndexFar < 5)
                            levelIndexFar++;
                    }
                    else //==
                    {
                        lastBid = bidDepth[levelIndex].price;
                        cumulativeAskSize += askDepth[levelIndex].qty;
                        if (levelIndex < 5)
                            levelIndex++;
                        cumulativeAskSizeFar += askDepthFar[levelIndexFar].qty; ;
                        if (levelIndexFar < 5)
                            levelIndexFar++;
                    }
                    if ((cumulativeAskSize >= config.size * P.joinFactor) | (cumulativeAskSize + cumulativeAskSizeFar >= config.size * GetJoinFactorAllVenues()))
                    {
                        ask = lastAsk;
                        reachedSizeTH = true;
                    }
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

            if (orders.orderInUse(buyFar))
            {
                orders.CancelOrder(buyFar);
            }

            if (orders.orderInUse(sellFar))
            {
                orders.CancelOrder(sellFar);
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

            if ((ask.qty < maxAskSize - config.size) && (ask.qty < 3 * config.size) && (sell.ask - ask.price < tickSize + 1e-9) && (ask.price < prevAsk.price + 1e-9))
            {
                PullQuotes();
                maxAskSize = 0;
                return;
            }
        }

        public override int GetNetPosition()
        {
            int netHolding = holding[quoteIndex] + holding[farIndex];
            if (leanIndex != farIndex)
                netHolding += holding[leanIndex];
            return netHolding;
        }

        public override void OnProcessMD(VIT vit)
        {
            try
            {
                orders.OnProcessMD();

                VI vi = new VI(vit.v, vit.i);
                int instrumentIndex = vi.i + API.n * vi.v;

                prevBid = bids[instrumentIndex];
                prevAsk = asks[instrumentIndex];

                bids[instrumentIndex] = API.GetBid(vi);
                asks[instrumentIndex] = API.GetAsk(vi);

                if (leanIndex != instrumentIndex)
                {
                    return;
                }

                bids[quoteIndex] = API.GetBid(new VI(quoteIndex / API.n, quoteIndex % API.n));
                asks[quoteIndex] = API.GetAsk(new VI(quoteIndex / API.n, quoteIndex % API.n));

                maxBidSize = bids[instrumentIndex].qty > maxBidSize ? bids[instrumentIndex].qty : maxBidSize;
                maxAskSize = asks[instrumentIndex].qty > maxAskSize ? asks[instrumentIndex].qty : maxAskSize;

                if (!API.PassedTradeStart())
                {
                    return;
                }

                if (API.GetStrategyStatus(stgID) == 0 || systemTradingMode == 'C')
                {
                    return;
                }

                if (activeStopOrders)
                {
                    hedging.EvaluateStops();
                }

                theo = API.GetImprovedCM(instrumentIndex);

                bool quote = !pricesAreEqual(theo, -11);
                double quoteTheo = theo + boxTargetPrice;
                //double quoteTheo = boxTargetPrice;

                var (quoteBid, quoteAsk) = GetBidAsk(quoteTheo, tickSize, (config.width / 2.0), quoteIndex);
                var (maxBid, minAsk) = getMinimumLonelinessConstrainedBidAsk(quoteInstrument, farInstrument);

                if (pricesAreEqual(maxBid, -11) || pricesAreEqual(minAsk, 11111))
                {
                    quote = false;
                }

                if (P.joinFactor > 0)
                {
                    quoteBid = Math.Min(quoteBid, maxBid);
                    quoteAsk = Math.Max(quoteAsk, minAsk);
                }

                if (quoteBid < -10 || quoteAsk > 1000)
                {
                    quote = false;
                }

                if (quoteAsk - quoteBid > config.width + 1e-9)
                {
                    quote = false;
                }

                if ((orders.orderInUse(buy) || orders.orderInUse(sell) || orders.orderInUse(buyFar) || orders.orderInUse(sellFar)) && !orders.orderInTransientState(buy) && !orders.orderInTransientState(sell) && !orders.orderInTransientState(buyFar) && !orders.orderInTransientState(sellFar))
                {
                    PullOnSizeDrop(bids[instrumentIndex], ref maxBidSize, asks[instrumentIndex], ref maxAskSize);

                    if (!quote)
                    {
                        if (orders.orderInUse(buy) && !orders.orderInTransientState(buy))
                        {
                            orders.CancelOrder(buy);
                        }

                        if (orders.orderInUse(sell) && !orders.orderInTransientState(sell))
                        {
                            orders.CancelOrder(sell);
                        }

                        if (orders.orderInUse(buyFar) && !orders.orderInTransientState(buyFar))
                        {
                            orders.CancelOrder(buyFar);
                        }

                        if (orders.orderInUse(sellFar) && !orders.orderInTransientState(sellFar))
                        {
                            orders.CancelOrder(sellFar);
                        }

                        return;
                    }

                    if (!pricesAreEqual(bids[leanIndex].price, bids[instrumentIndex].price) || !pricesAreEqual(asks[leanIndex].price, asks[instrumentIndex].price))
                    {
                        if (orders.orderInUse(buy))
                        {
                            if (!orders.orderInTransientState(buy))
                            {
                                orders.CancelOrder(buy);
                            }
                        }

                        if (orders.orderInUse(buyFar))
                        {
                            if (!orders.orderInTransientState(buyFar))
                            {
                                orders.CancelOrder(buyFar);
                            }
                        }

                        if (orders.orderInUse(sellFar))
                        {
                            if (!orders.orderInTransientState(sellFar))
                            {
                                orders.CancelOrder(sellFar);
                            }
                        }
                    }
                }

                if (!quote)
                {
                    return;
                }

                int netHolding = GetNetPosition();

                if (netHolding != 0)
                {
                    hedging.Hedge();
                    return;
                }

                if ((quoteBid >= asks[quoteIndex].price) & (asks[quoteIndex].qty > 0))
                {
                    CancelStrategy(String.Format("quote_bid={0} >= best_offer={1}", quoteBid, asks[quoteIndex].price));
                    return;
                }

                if ((quoteAsk <= bids[quoteIndex].price) & (bids[quoteIndex].qty > 0))
                {
                    CancelStrategy(String.Format("quote_ask={0} <= best_bid={1}", quoteAsk, bids[quoteIndex].price));
                    return;
                }

                int quoteSizeBid = config.size;
                int quoteSizeAsk = config.size;

                if (config.asymmetricQuoting.HasValue && config.asymmetricQuoting.Value && P.enableAsymmetricQuoting)
                {
                    if (API.GetImprovedCM(quoteIndex) > (quoteBid + quoteAsk) / 2)
                    //if (quoteIndex % 2 == 0)
                    {
                        quoteSizeBid = quoteSizeBid * 2 + 1;
                    }
                    else
                    {
                        quoteSizeAsk = quoteSizeAsk * 2 + 1;
                    }
                }

                if (!orders.orderInUse(buy) && !orders.orderInTransientState(buy) && quote)
                {
                    orders.SendOrder(buy, quoteIndex, Side.BUY, quoteBid, quoteSizeBid, "MM");
                }

                if (!orders.orderInUse(sell) && !orders.orderInTransientState(sell) && quote)
                {
                    orders.SendOrder(sell, quoteIndex, Side.SELL, quoteAsk, quoteSizeAsk, "MM");
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
            throttler.updateTimespan(GetQuoteThrottleSeconds());
            throttler.updateMaxVolume(GetQuoteThrottleVolume());
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
                if (deal.source == "FW")
                    return;

                int amount = deal.isBuy ? deal.amount : -deal.amount;

                int instrumentIndex = deal.index + API.n * deal.VenueID;

                holding[instrumentIndex] += amount;

                hedging.PropagateToStopOrder(deal.internalOrderNumber);
                hedging.ManagePendingOrders(deal);

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

            hedging.OnOrder(ord);

            if (!orders.orderInTransientState(ord))
            {
                orders.OnOrder(ord);
            }
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
                } //end else
            } // end foreach

            return (valueChanged, ret);
        }
    }
}
