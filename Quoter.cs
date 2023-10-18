using Detail;
using StrategyLib;
using System;
using System.Collections.Generic;
using KGClasses;
using System.Xml;

namespace Config
{
    public class QuoterConfig
    {
        public double width;
        public int size;
        public string leanInstrument;
        public string quoteInstrument;
        public string quoteFarInstrument;
        public string icsInstrument;

        public QuoterConfig(string file, API api)
        {
            try
            {
                api.Log("-->Config");
                XmlDocument doc = new XmlDocument();
                doc.Load(file);
                api.Log("AfterLoad");
                width = Double.Parse(doc.DocumentElement.SelectSingleNode("/strategyRunner/width").InnerText);
                size = Int32.Parse(doc.DocumentElement.SelectSingleNode("/strategyRunner/size").InnerText);
                leanInstrument = doc.DocumentElement.SelectSingleNode("/strategyRunner/lean").InnerText;
                quoteInstrument = doc.DocumentElement.SelectSingleNode("/strategyRunner/quoteNear").InnerText;

                if (doc.DocumentElement.ChildNodes.Count > 4)
                    quoteFarInstrument = doc.DocumentElement.SelectSingleNode("/strategyRunner/quoteFar").InnerText;

                if (doc.DocumentElement.ChildNodes.Count > 5)
                    icsInstrument = doc.DocumentElement.SelectSingleNode("/strategyRunner/ics").InnerText;

                api.Log(String.Format("config xml={0}", doc.OuterXml));
                api.Log("<--Config");
            }
            catch (Exception e)
            {
                api.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }
    }
}

namespace StrategyRunner
{
    public class Quoter : Strategy
    {
        public Config.QuoterConfig config;

        double theo;

        double maxBidSize;
        double maxAskSize;

        public int icsIndex;

        public double tickSize;

        public int numAllInstruments;
        public int numInstrumentsInVenue;

        bool activeStopOrders = false;

        VI leanInstrument;
        VI quoteInstrument;
        VI quoteFarInstrument;
        VI icsInstrument;

        VIT quoteInstrumentT;
        VIT quoteFarInstrumentT;

        KGOrder buy;
        KGOrder sell;

        KGOrder buyFar;
        KGOrder sellFar;

        DepthElement prevBid;
        DepthElement prevAsk;

        List<VI> instruments;
        
        public Hedging hedging;
        BaseSpreads baseSpreads;

        public Quoter(API api, string configFile)
        {
            try
            {
                API = api;
                API.Log("-->strategy:" + configFile);

                numAllInstruments = API.N;
                numInstrumentsInVenue = API.n;

                holding = new int[numAllInstruments];
                bids = new DepthElement[numAllInstruments];
                asks = new DepthElement[numAllInstruments];
                theos = new double[numAllInstruments];

                for (int i = 0; i < numAllInstruments; i++)
                {
                    holding[i] = 0;
                    bids[i] = new DepthElement(-11, 0);
                    asks[i] = new DepthElement(11111, 0);
                    theos[i] = -11;
                }

                config = new Config.QuoterConfig(configFile, api);

                instruments = new List<VI>();

                leanIndex = API.GetSecurityIndex(config.leanInstrument);
                quoteIndex = API.GetSecurityIndex(config.quoteInstrument);

                if (config.quoteFarInstrument != null)
                    quoteFarIndex = API.GetSecurityIndex(config.quoteFarInstrument);
                else
                    quoteFarIndex = -1;

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

                if (quoteFarIndex != -1)
                {
                    int quoteFarVenue = quoteFarIndex / numInstrumentsInVenue;
                    int quoteFarIndexGlobal = quoteFarIndex % numInstrumentsInVenue;
                    quoteFarInstrument = new VI(quoteFarVenue, quoteFarIndexGlobal);
                    quoteFarInstrumentT = new VIT(quoteFarVenue, quoteFarIndexGlobal, 0, 0);
                    instruments.Add(quoteFarInstrument);
                }

                quoteInstrumentT = new VIT(quoteVenue, quoteIndexGlobal, 0, 0);

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
                baseSpreads = new BaseSpreads(this);

                API.Log("-->Start strategy:");
                API.StartStrategy(ref stgID, strategyOrders, instruments, 0);

                API.Log("<--strategy");
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private double GetOffset() //this assumes stgID is per expiry, might wanna add some config instead
        {
            switch (stgID)
            {
                case 1:
                    return P.eus1;
                case 2:
                    return P.eus2;
                case 3:
                    return P.eus3;
                case 4:
                    return P.eus4;
                case 5:
                    return P.eus5;
                case 6:
                    return P.eus6;
                default:
                    throw new Exception("offset index out of bounds");
            }
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
            return Math.Abs(price1 - price2) < 1e-4;
        }

        private bool shouldQuote(VIT viLean)
        {
            if (P.joinFactor == 0)
            {
                return true;
            }

            List<DepthElement> bidDepth = API.GetBidDepth(viLean);
            List<DepthElement> askDepth = API.GetAskDepth(viLean);

            int bidVolume = 0;
            int askVolume = 0;

            for (int levelIndex = 0; levelIndex < 5; ++levelIndex)
            {
                double width = askDepth[levelIndex].price - bidDepth[levelIndex].price;
                if (width > config.width)
                {
                    break;
                }
                bidVolume += bidDepth[levelIndex].qty;
                askVolume += askDepth[levelIndex].qty;
            }

            if (bidVolume < config.size * P.joinFactor || askVolume < config.size * P.joinFactor)
            {
                return false;
            }

            return true;
        }

        private (double, double) getMinimumLonelinessConstrainedBidAsk(VIT instrument)
        {
            List<DepthElement> bidDepth = API.GetBidDepth(instrument);
            List<DepthElement> askDepth = API.GetAskDepth(instrument);

            int bidVolume = 0;
            int askVolume = 0;

            double bid = -11;
            double ask = 11111;

            for (int levelIndex = 0; levelIndex < 5; ++levelIndex)
            {
                bidVolume += bidDepth[levelIndex].qty;
                if (bidVolume >= config.size * P.joinFactor)
                {
                    bid = bidDepth[levelIndex].price;
                    break;
                }
            }


            for (int levelIndex = 0; levelIndex < 5; ++levelIndex)
            {
                askVolume += askDepth[levelIndex].qty;
                if (askVolume >= config.size * P.joinFactor)
                {
                    ask = askDepth[levelIndex].price;
                    break;
                }
            }

            return (bid, ask);
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

        public override void OnProcessMD(VIT vi)
        {
            try
            {
                int instrumentIndex = vi.i + API.n * vi.v;

                prevBid = bids[instrumentIndex];
                prevAsk = asks[instrumentIndex];

                bids[instrumentIndex] = API.GetBid(vi);
                asks[instrumentIndex] = API.GetAsk(vi);

                if (leanIndex != instrumentIndex)
                {
                    return;
                }

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

                baseSpreads.ManageBaseSpreads();

                theo = GetVwap(bids[instrumentIndex], asks[instrumentIndex]);
                theos[instrumentIndex] = theo; //TODO: remove once improvedcms are present in the rec files

                bool quote = shouldQuote(vi) && !pricesAreEqual(theo, -11);

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

                double offset = GetOffset();

                double quoteTheo = theo + offset;

                var (quoteBid, quoteAsk) = GetBidAsk(quoteTheo, tickSize, (config.width / 2.0), quoteIndex);
                var (maxBid, minAsk) = getMinimumLonelinessConstrainedBidAsk(quoteInstrumentT);

                if (pricesAreEqual(maxBid, -11) || pricesAreEqual(minAsk, 11111))
                {
                    quote = false;
                }

                if (P.joinFactor > 0)
                {
                    quoteBid = Math.Min(quoteBid, maxBid);
                    quoteAsk = Math.Max(quoteAsk, minAsk);
                }

                if (quoteBid < -10 || quoteAsk > 100)
                {
                    quote = false;
                }

                if (quoteAsk - quoteBid > config.width + 1e-9)
                {
                    quote = false;
                }

                if (!orders.orderInUse(buy) && !orders.orderInTransientState(buy) && quote)
                {
                    orders.SendOrder(buy, quoteIndex, Side.BUY, quoteBid, config.size, "MM");
                }

                if (!orders.orderInUse(sell) && !orders.orderInTransientState(sell) && quote)
                {
                    orders.SendOrder(sell, quoteIndex, Side.SELL, quoteAsk, config.size, "MM");
                }

                if (quoteFarIndex != -1)
                {
                    bool quoteFar = true;

                    var (quoteFarBid, quoteFarAsk) = GetBidAsk(quoteTheo, tickSize, (config.width / 2.0), quoteFarIndex);
                    var (maxFarBid, minFarAsk) = getMinimumLonelinessConstrainedBidAsk(quoteFarInstrumentT);

                    if (pricesAreEqual(maxFarBid, -11) || pricesAreEqual(minFarAsk, 11111))
                    {
                        quoteFar = false;
                    }

                    if (P.joinFactor > 0)
                    {
                        quoteFarBid = Math.Min(quoteFarBid, maxFarBid);
                        quoteFarAsk = Math.Max(quoteFarAsk, minFarAsk);
                    }

                    if (quoteFarBid < -10 || quoteFarAsk > 100)
                    {
                        quoteFar = false;
                    }

                    if (quoteFarAsk - quoteFarBid > config.width + 1e-9)
                    {
                        quoteFar = false;
                    }

                    if (!orders.orderInUse(buyFar) && !orders.orderInTransientState(buyFar) && quoteFar)
                    {
                        orders.SendOrder(buyFar, quoteFarIndex, Side.BUY, quoteBid, config.size, "MM");
                    }

                    if (!orders.orderInUse(sellFar) && !orders.orderInTransientState(sellFar) && quoteFar)
                    {
                        orders.SendOrder(sellFar, quoteFarIndex, Side.SELL, quoteAsk, config.size, "MM");
                    }
                }
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        public override void OnParamsUpdate()
        {
            baseSpreads.OnUpdatedParams();
        }

        private double GetVwap(DepthElement bid, DepthElement ask)
        {
            if (bid.qty == 0 || ask.qty == 0)
            {
                return -11;
            }

            return (bid.price * ask.qty + ask.price * bid.qty) / (bid.qty + ask.qty);
        }

        public override void OnImprovedCM(int index, double CMPrice)
        {
            if (index != leanIndex)
            {
                return;
            }

            theos[index] = CMPrice;
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
                int amount = deal.isBuy ? deal.amount : -deal.amount;

                int instrumentIndex = deal.index + API.n * deal.VenueID;

                holding[instrumentIndex] += amount;

                hedging.PropagateToStopOrder(deal.internalOrderNumber);
                hedging.ManagePendingOrders(deal);

                baseSpreads.ManagePendingOrders(deal);
                baseSpreads.GetPosition();
                baseSpreads.UpdateBaseSpreadsAveragePrice(amount, deal.price, instrumentIndex);

                if (deal.source == "MM" && (instrumentIndex == quoteIndex || instrumentIndex == quoteFarIndex))
                {
                    if (instrumentIndex == quoteIndex)
                    {
                        hedging.Hedge(-amount, Source.NEAR);
                    }
                    else
                    {
                        hedging.Hedge(-amount, Source.FAR);
                    }
                }
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

            if (ord.orderStatus == 9)
            {
                CancelStrategy(String.Format("order {0} rejected", ord.internalOrderNumber));
                return;
            }
        }
    }
}
