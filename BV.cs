using System;
using System.Collections.Generic;
using KGClasses;
using StrategyLib;
using System.Timers;
using Detail;
using System.Xml;

namespace Detail
{
    public enum Direction
    {
        NEUTRAL = 0,
        BUY = 1,
        SELL = 2
    }

    public enum HedgeReason
    {
        TIMEOUT = 0,
        PNL_DROP = 1
    }
}

namespace BVConfig
{
    class BVConfig
    {
        public string nearInstrument;
        public string farInstrument;
        public string leanInstrument;
        public int limitPlusSize;
        public double defaultBaseSpread;

        public BVConfig(string file, API api)
        {
            try
            {
                api.Log("-->Config");
                XmlDocument doc = new XmlDocument();
                doc.Load(file);
                api.Log("AfterLoad");
                nearInstrument = doc.DocumentElement.SelectSingleNode("/strategyRunner/nearInstrument").InnerText;
                farInstrument = doc.DocumentElement.SelectSingleNode("/strategyRunner/farInstrument").InnerText;
                leanInstrument = doc.DocumentElement.SelectSingleNode("/strategyRunner/leanInstrument").InnerText;
                limitPlusSize = Int32.Parse(doc.DocumentElement.SelectSingleNode("/strategyRunner/limitPlusSize").InnerText);
                defaultBaseSpread = Double.Parse(doc.DocumentElement.SelectSingleNode("/strategyRunner/defaultBaseSpread").InnerText);
                    

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
    public class BV : Strategy
    {
        public double BVBuyPrice;
        public double BVSellPrice; //POTENTIALLY BVSellPrice < BVBuyPrice IF THERE'S ARBITRAGE
        public int BVBuyIndex;
        public int BVSellIndex;

        public double limitBVSellPrice;
        public double limitBVBuyPrice;
        public int limitBVBuyIndex;
        public int limitBVSellIndex;

        private BVConfig.BVConfig config;

        public int numAllInstruments;
        public int numInstrumentsInVenue;

        VI nearInstrument;
        VI farInstrument;
        VI leanInstrument;

        KGOrder buy;
        KGOrder sell;

        KGOrder buyFar;
        KGOrder sellFar;

        KGOrder limitBuy;
        KGOrder limitSell;

        KGOrder limitBuyFar;
        KGOrder limitSellFar;

        List<VI> instruments;
        Dictionary<int /*orderId*/, int /*amount*/> pendingOrders;

        Throttler.Throttler bvThrottler;

        int pendingBuys = 0;
        int pendingSells = 0;

        Timer timeout;

        Hedging hedging;

        double positionValue;
        double cashflow;
        double pnl;

        Dictionary<int, int> pendingTrades;

        public BV(API api, string configFile)
        {
            try
            {
                API = api;
                API.Log("-->strategy:" + configFile);

                systemTradingMode = 'C';

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

                config = new BVConfig.BVConfig(configFile, api);

                limitPlusSize = config.limitPlusSize;
                boxTargetPrice = config.defaultBaseSpread;

                instruments = new List<VI>();
                pendingOrders = new Dictionary<int, int>();

                quoteIndex = API.GetSecurityIndex(config.nearInstrument);
                quoteFarIndex = API.GetSecurityIndex(config.farInstrument);
                leanIndex = API.GetSecurityIndex(config.leanInstrument);

                int nearVenue = quoteIndex / numInstrumentsInVenue;
                int nearIndexGlobal = quoteIndex % numInstrumentsInVenue;

                int farVenue = quoteFarIndex / numInstrumentsInVenue;
                int farIndexGlobal = quoteFarIndex % numInstrumentsInVenue;

                int leanVenue = leanIndex / numInstrumentsInVenue;
                int leanIndexGlobal = leanIndex % numInstrumentsInVenue;

                nearInstrument = new VI(nearVenue, nearIndexGlobal);
                farInstrument = new VI(farVenue, farIndexGlobal);
                leanInstrument = new VI(leanVenue, leanIndexGlobal);

                instruments.Add(nearInstrument);
                instruments.Add(farInstrument);
                instruments.Add(leanInstrument);

                strategyOrders = new List<KGOrder>();

                buy = new KGOrder();
                strategyOrders.Add(buy);
                sell = new KGOrder();
                strategyOrders.Add(sell);

                buyFar = new KGOrder();
                strategyOrders.Add(buyFar);
                sellFar = new KGOrder();
                strategyOrders.Add(sellFar);

                limitBuy = new KGOrder();
                strategyOrders.Add(limitBuy);
                limitSell = new KGOrder();
                strategyOrders.Add(limitSell);
                limitBuyFar = new KGOrder();
                strategyOrders.Add(limitBuyFar);
                limitSellFar = new KGOrder();
                strategyOrders.Add(limitSellFar);

                TimeSpan tBv = new TimeSpan(0, 0, 0, 0, P.bvThrottleSeconds * 1000);

                bvThrottler = new Throttler.Throttler(P.bvThrottleVolume, tBv);

                timeout = new Timer();
                timeout.Elapsed += OnTimeout;
                timeout.AutoReset = false;

                pendingTrades = new Dictionary<int, int>();

                orders = new Orders(this);
                hedging = new Hedging(this);

                API.Log("-->Start strategy:");
                API.StartStrategy(ref stgID, strategyOrders, instruments, 0);

                API.Log("<--strategy");
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
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

        public override void OnFlush()
        {

        }

        private void CancelStrategy(string reason)
        {
            API.SetStrategyStatus(stgID, 0);
            API.CancelAllOrders(stgID);
            API.SendAlertBeep();
            API.Log(String.Format("CANCEL STG {0}: {1}", stgID, reason));
            API.SendToRemote(String.Format("CANCEL STG {0}: {1}", stgID, reason), KGConstants.EVENT_ERROR);
        }

        private int GetNetPosition()
        {
            return holding[quoteIndex] + holding[quoteFarIndex] + holding[leanIndex];
        }

        private int GetQuotedPosition()
        {
            return holding[quoteIndex] + holding[quoteFarIndex];
        }

        private int Buy(int n, string source, int index, double price)
        {
            int orderId = orders.SendOrder(buy, index, Side.BUY, price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
        }
        private int Sell(int n, string source, int index, double price)
        {
            int orderId = orders.SendOrder(sell, index, Side.SELL, price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
        }

        private bool isBuyPreferred(double BVPrice)
        {
            if (BVPrice - API.GetImprovedCM(leanIndex) < boxTargetPrice - P.creditOffset)
                return true;
            else
                return false;
        }

        private bool isSellPreferred(double BVPrice)
        {
            if (BVPrice - API.GetImprovedCM(leanIndex) > boxTargetPrice + P.creditOffset)
                return true;
            else
                return false;
        }

        private void HedgeLeftovers(HedgeReason reason)
        {
            int volume = pendingBuys - pendingSells;
            if (volume == 0)
            {
                return;
            }

            foreach (var deal in pendingOrders)
            {
                orders.CancelOrder(deal.Key);
            }

            Log(String.Format("BV: hedging leftovers, volume={0}, reason={1}", volume, reason));
            hedging.Hedge(pendingBuys - pendingSells, Source.NEAR);
            pendingBuys = 0;
            pendingSells = 0;
            cashflow = 0;
        }

        private void OnTimeout(object sender, ElapsedEventArgs e)
        {
            HedgeLeftovers(HedgeReason.TIMEOUT);
            pendingTrades.Clear();
        }

        private void TakeCross()
        {
            if (bids[quoteIndex].price >= asks[quoteFarIndex].price)
            {
                BVSellPrice = bids[quoteIndex].price;
                BVBuyPrice = asks[quoteFarIndex].price;

                if (GetQuotedPosition() < -P.bvMaxOutstandingOutrights)
                    return;

                if (holding[leanIndex] > P.maxOutrights)
                    return;

                timeout.Stop();
                timeout.Start();

                int availableVolume = Math.Min(API.GetBid(nearInstrument).qty, API.GetAsk(farInstrument).qty);
                int volume = Math.Min(availableVolume, P.maxCrossVolume);

                if (volume == 0)
                {
                    return;
                }

                if (Math.Abs(holding[quoteIndex] - volume) > P.maxOutrights || Math.Abs(holding[quoteFarIndex] + volume) > P.maxOutrights)
                {
                    return;
                }

                if (!bvThrottler.addTrade(volume))
                    return;

                if (isSellPreferred(BVSellPrice))
                {
                    BVSellIndex = quoteIndex;
                    BVBuyIndex = quoteFarIndex;
                    Sell(volume, "BV", BVSellIndex, BVSellPrice);
                    Buy(volume, "BV", BVBuyIndex, BVBuyPrice);
                }
                else if (isBuyPreferred(BVBuyPrice))
                {
                    BVSellIndex = quoteIndex;
                    BVBuyIndex = quoteFarIndex;
                    int orderId = Buy(volume, "BV", BVBuyIndex, BVBuyPrice);
                    pendingTrades[BVBuyIndex] = -volume;
                }

                pendingBuys += volume;
                pendingSells += volume;
            }

            if (asks[quoteIndex].price <= bids[quoteFarIndex].price)
            {
                BVSellPrice = bids[quoteFarIndex].price;
                BVBuyPrice = asks[quoteIndex].price;

                if (GetQuotedPosition() > P.bvMaxOutstandingOutrights)
                    return;

                if (holding[leanIndex] < -P.maxOutrights)
                    return;

                timeout.Stop();
                timeout.Start();

                int availableVolume = Math.Min(API.GetAsk(nearInstrument).qty, API.GetBid(farInstrument).qty);
                int volume = Math.Min(availableVolume, P.maxCrossVolume);

                if (volume == 0)
                {
                    return;
                }

                if (Math.Abs(holding[quoteFarIndex] + volume) > P.maxOutrights || Math.Abs(holding[quoteIndex] - volume) > P.maxOutrights)
                {
                    return;
                }

                if (!bvThrottler.addTrade(volume))
                    return;

                if (isBuyPreferred(BVBuyPrice))
                {
                    BVSellIndex = quoteFarIndex;
                    BVBuyIndex = quoteIndex;
                    Buy(volume, "BV", BVBuyIndex, BVBuyPrice);
                    Sell(volume, "BV", BVSellIndex, BVSellPrice);
                }
                else if (isSellPreferred(BVSellPrice))
                {
                    BVSellIndex = quoteFarIndex;
                    BVBuyIndex = quoteIndex;
                    int orderId = Sell(volume, "BV", BVSellIndex, BVSellPrice);
                    pendingTrades[BVSellIndex] = volume;
                }

                pendingBuys += volume;
                pendingSells += volume;
            }
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private void SendLimitOrders()
        {
            int ordId;
            if (isBuyPreferred(bids[quoteIndex].price) && (bids[quoteFarIndex].price <= bids[quoteIndex].price))
            {
                if ((GetQuotedPosition() > -P.bvMaxOutstandingOutrights) && (holding[leanIndex] > -P.maxOutrights) && (!orders.orderInUse(limitBuyFar)))
                {
                    limitBVBuyPrice = bids[quoteIndex].price;
                    limitBVBuyIndex = quoteFarIndex;
                    limitBVSellIndex = quoteIndex;
                    
                    int volume = Math.Min(bids[quoteIndex].qty / 2, P.maxCrossVolume);
                    if (volume > 0)
                        ordId = orders.SendOrder(limitBuyFar, limitBVBuyIndex, Side.BUY, limitBVBuyPrice, volume, "LIMIT_BV");
                }
            }
            if (isSellPreferred(asks[quoteIndex].price) && (asks[quoteFarIndex].price >= asks[quoteIndex].price))
            {
                if ((GetQuotedPosition() < P.bvMaxOutstandingOutrights) && (holding[leanIndex] < P.maxOutrights) && (!orders.orderInUse(limitSellFar)))
                {
                    limitBVSellPrice = asks[quoteIndex].price;
                    limitBVSellIndex = quoteFarIndex;
                    limitBVBuyIndex = quoteIndex;

                    int volume = Math.Min(asks[quoteIndex].qty / 2, P.maxCrossVolume);
                    if (volume > 0)
                        ordId = orders.SendOrder(limitSellFar, limitBVSellIndex, Side.SELL, limitBVSellPrice, volume, "LIMIT_BV");
                }
            }
        }

        private void CancelOnPriceMove(int instrumentIndex)
        {
            if ((!pricesAreEqual(limitBVBuyPrice, bids[quoteIndex].price)) && (orders.orderInUse(limitBuyFar)))
                orders.CancelOrder(limitBuyFar);
            else if ((!pricesAreEqual(limitBVSellPrice, asks[quoteIndex].price)) && (orders.orderInUse(limitSellFar)))
                orders.CancelOrder(limitSellFar);
        }

        public override void OnProcessMD(VIT vit)
        {
            try
            {
                VI vi = new VI(vit.v, vit.i);
                int instrumentIndex = vi.i + API.n * vi.v;

                bids[instrumentIndex] = API.GetBid(vi);
                asks[instrumentIndex] = API.GetAsk(vi);
                double theo = API.GetImprovedCM(instrumentIndex);

                positionValue = (pendingSells - pendingBuys) * theo;
                pnl = (cashflow + positionValue) * 2500;

                if (pnl < -P.bvMaxLoss)
                {
                    HedgeLeftovers(HedgeReason.PNL_DROP);
                    return;
                }

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

                if (GetNetPosition() != 0)
                    return;

                if (P.bvEnabled && pendingBuys == 0 && pendingSells == 0)
                {
                    TakeCross();
                }

                if (P.limitBvEnabled)
                {
                    SendLimitOrders();
                    CancelOnPriceMove(instrumentIndex);
                }
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        public override void OnParamsUpdate()
        {
            bvThrottler.updateMaxVolume(P.bvThrottleVolume);
            bvThrottler.updateTimespan(P.bvThrottleSeconds);

            if (P.bvTimeoutSeconds > 0)
            {
                timeout.Interval = P.bvTimeoutSeconds * 1000;
            }
        }

        private void CheckPending(int instrumentIndex)
        {
            if (pendingTrades.ContainsKey(instrumentIndex))
            {
                int volume = pendingTrades[instrumentIndex];

                if (volume > 0)
                    Buy(volume, "BV", BVBuyIndex, BVBuyPrice);
                else if (volume < 0)
                    Sell(-volume, "BV", BVSellIndex, BVSellPrice);

                pendingTrades.Remove(instrumentIndex);
            }
        }

        private void ExecuteOtherLegAtMarket(int instrumentIndex, int amount)
        {
            timeout.Stop();
            timeout.Start();
            if (amount < 0)
            {
                Buy(-amount, "ON_LIMIT_BV", limitBVBuyIndex, limitBVSellPrice);
                pendingBuys += -amount;
            }
            else if (amount > 0)
            {
                Sell(amount, "ON_LIMIT_BV", limitBVSellIndex, limitBVBuyPrice);
                pendingSells += amount;
            }
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
                if (deal.source == "FW")
                    return;

                if (pendingOrders.ContainsKey(deal.internalOrderNumber))
                {
                    pendingOrders[deal.internalOrderNumber] -= deal.amount;
                    if (pendingOrders[deal.internalOrderNumber] == 0)
                    {
                        pendingOrders.Remove(deal.internalOrderNumber);
                    }
                }

                int amount = deal.isBuy ? deal.amount : -deal.amount;

                int instrumentIndex = deal.index + API.n * deal.VenueID;

                holding[instrumentIndex] += amount;

                cashflow += amount * deal.price * -1;

                CheckPending(instrumentIndex);

                if (deal.source != "LIMIT_BV")
                {
                    if (deal.isBuy)
                    {
                        pendingBuys -= deal.amount;
                    }
                    else
                    {
                        pendingSells -= deal.amount;
                    }

                    return;
                }

                ExecuteOtherLegAtMarket(instrumentIndex, amount);
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