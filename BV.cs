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
        private BVConfig.BVConfig config;

        public int numAllInstruments;
        public int numInstrumentsInVenue;

        VI nearInstrument;
        VI farInstrument;
        VIT nearInstrumentT;
        VIT farInstrumentT;

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

        Timer timeout; //todo: stop on deal when other leg is filled
        //todo: hedge limitbv too

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

                instruments = new List<VI>();
                pendingOrders = new Dictionary<int, int>();

                quoteIndex = API.GetSecurityIndex(config.nearInstrument);
                quoteFarIndex = API.GetSecurityIndex(config.farInstrument);
                leanIndex = API.GetSecurityIndex(config.leanInstrument);

                int nearVenue = quoteIndex / numInstrumentsInVenue;
                int nearIndexGlobal = quoteIndex % numInstrumentsInVenue;

                int farVenue = quoteFarIndex / numInstrumentsInVenue;
                int farIndexGlobal = quoteFarIndex % numInstrumentsInVenue;

                nearInstrument = new VI(nearVenue, nearIndexGlobal);
                farInstrument = new VI(farVenue, farIndexGlobal);
                nearInstrumentT = new VIT(nearVenue, nearIndexGlobal, 0, 0);
                farInstrumentT = new VIT(farVenue, farIndexGlobal, 0, 0);

                instruments.Add(nearInstrument);
                instruments.Add(farInstrument);

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

        private int SellNear(int n, string source)
        {
            int orderId = orders.SendOrder(sell, quoteIndex, Side.SELL, bids[quoteIndex].price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
        }

        private int BuyNear(int n, string source)
        {
            int orderId = orders.SendOrder(buy, quoteIndex, Side.BUY, asks[quoteIndex].price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
        }

        private int SellFar(int n, string source)
        {
            int orderId = orders.SendOrder(sellFar, quoteFarIndex, Side.SELL, bids[quoteFarIndex].price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
        }

        private int BuyFar(int n, string source)
        {
            int orderId = orders.SendOrder(buyFar, quoteFarIndex, Side.BUY, asks[quoteFarIndex].price, n, source);
            pendingOrders[orderId] = n;
            return orderId;
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

        private void TakeCross(Direction direction)
        {
            if (bids[quoteIndex].price == asks[quoteFarIndex].price)
            {
                timeout.Stop();
                timeout.Start();

                int availableVolume = Math.Min(API.GetBid(nearInstrumentT).qty, API.GetAsk(farInstrumentT).qty);
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

                if (direction == Direction.SELL)
                {
                    SellNear(volume, "BV");
                    BuyFar(volume, "BV");
                }
                else
                {
                    int orderId = SellNear(volume, "BV");
                    pendingTrades[quoteIndex] = volume;
                }

                pendingBuys += volume;
                pendingSells += volume;
            }

            if (asks[quoteIndex].price == bids[quoteFarIndex].price)
            {
                timeout.Stop();
                timeout.Start();

                int availableVolume = Math.Min(API.GetAsk(nearInstrumentT).qty, API.GetBid(farInstrumentT).qty);
                int volume = Math.Min(availableVolume, P.maxCrossVolume);

                if (volume == 0)
                {
                    return;
                }

                if (Math.Abs(holding[quoteFarIndex] - volume) > P.maxOutrights || Math.Abs(holding[quoteIndex] + volume) > P.maxOutrights)
                {
                    return;
                }

                if (!bvThrottler.addTrade(volume))
                    return;

                if (direction == Direction.BUY)
                {
                    BuyNear(volume, "BV");
                    SellFar(volume, "BV");
                }
                else
                {
                    int orderId = BuyNear(volume, "BV");
                    pendingTrades[quoteIndex] = -volume;
                }

                pendingBuys += volume;
                pendingSells += volume;
            }
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private Direction GetDirection()
        {
            double spreadPrice = boxTargetPrice;

            double quotePrice = bids[quoteFarIndex].price;

            double leanPrice = API.GetImprovedCM(leanIndex);

            if (quotePrice - leanPrice < spreadPrice - P.creditOffset)
            {
                return Direction.BUY;
            }
            else if (quotePrice - leanPrice > spreadPrice + P.creditOffset)
            {
                return Direction.SELL;
            }
            else
            {
                return Direction.NEUTRAL;
            }
        }

        private void InsertAtMid(int instrumentIndex)
        {
            if (instrumentIndex == quoteIndex)
            {
                double mid = (asks[instrumentIndex].price + bids[instrumentIndex].price) / 2.0;

                if (!orders.orderInUse(limitBuyFar) && pricesAreEqual(mid, bids[quoteFarIndex].price) && GetDirection() == Direction.BUY)
                {
                    int orderId = orders.SendOrder(limitBuyFar, quoteFarIndex, Side.BUY, bids[quoteFarIndex].price, Math.Min(bids[instrumentIndex].qty, P.maxCrossVolume), "LIMIT_BV");
                }
                else if (!orders.orderInUse(limitSellFar) && pricesAreEqual(mid, asks[quoteFarIndex].price) && GetDirection() == Direction.SELL)
                {
                    int orderId = orders.SendOrder(limitSellFar, quoteFarIndex, Side.SELL, asks[quoteFarIndex].price, Math.Min(asks[instrumentIndex].qty, P.maxCrossVolume), "LIMIT_BV");
                }
            }
            else if (instrumentIndex == quoteFarIndex)
            {
                double mid = (asks[instrumentIndex].price + bids[instrumentIndex].price) / 2.0;

                if (!orders.orderInUse(limitBuy) && pricesAreEqual(mid, bids[quoteIndex].price) && GetDirection() == Direction.BUY)
                {
                    int orderId = orders.SendOrder(limitBuy, quoteIndex, Side.BUY, bids[quoteIndex].price, Math.Min(bids[instrumentIndex].qty, P.maxCrossVolume), "LIMIT_BV");
                }
                else if (!orders.orderInUse(limitSell) && pricesAreEqual(mid, asks[quoteIndex].price) && GetDirection() == Direction.SELL)
                {
                    int orderId = orders.SendOrder(limitSell, quoteIndex, Side.SELL, asks[quoteIndex].price, Math.Min(asks[instrumentIndex].qty, P.maxCrossVolume), "LIMIT_BV");
                }
            }
        }

        private void CancelOnPriceMove(int instrumentIndex)
        {
            //TODO: we can get filled on the order on price move on the exchange it's working even if the price is still the mid of the other exchange
            //might wanna add some loneliness or improvedcm criterion

            if (instrumentIndex == quoteIndex)
            {
                double mid = (asks[instrumentIndex].price + bids[instrumentIndex].price) / 2.0;

                if (orders.orderInUse(limitBuyFar) && !pricesAreEqual(mid, bids[quoteFarIndex].price))
                {
                    orders.CancelOrder(limitBuyFar);
                }
                else if (orders.orderInUse(limitSellFar) && !pricesAreEqual(mid, asks[quoteFarIndex].price))
                {
                    orders.CancelOrder(limitSellFar);
                }
            }
            else if (instrumentIndex == quoteFarIndex)
            {
                double mid = (asks[instrumentIndex].price + bids[instrumentIndex].price) / 2.0;

                if (orders.orderInUse(limitBuy) && !pricesAreEqual(mid, bids[quoteIndex].price))
                {
                    orders.CancelOrder(limitBuy);
                }
                else if (orders.orderInUse(limitSell) && !pricesAreEqual(mid, asks[quoteIndex].price))
                {
                    orders.CancelOrder(limitSell);
                }
            }
        }

        public override void OnProcessMD(VIT vi)
        {
            try
            {
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

                if (P.bvEnabled && pendingBuys == 0 && pendingSells == 0)
                {
                    TakeCross(GetDirection());
                }

                if (P.limitBvEnabled)
                {
                    InsertAtMid(instrumentIndex);
                    CancelOnPriceMove(instrumentIndex);
                }

                if (activeStopOrders)
                {
                    hedging.EvaluateStops();
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
                if (instrumentIndex == quoteIndex)
                {
                    if (volume > 0)
                    {
                        BuyFar(volume, "BV");
                    }
                    else if (volume < 0)
                    {
                        SellFar(-volume, "BV");
                    }
                }
                else if (instrumentIndex == quoteFarIndex)
                {
                    if (volume > 0)
                    {
                        BuyNear(volume, "BV");
                    }
                    else if (volume < 0)
                    {
                        SellNear(-volume, "BV");
                    }
                }
                pendingTrades.Remove(instrumentIndex);
            }
        }

        private void ExecuteOtherLegAtMarket(int instrumentIndex, int amount)
        {
            if (instrumentIndex == quoteIndex)
            {
                if (amount < 0)
                {
                    BuyFar(-amount, "ON_LIMIT_BV");
                }
                else if (amount > 0)
                {
                    SellFar(amount, "ON_LIMIT_BV");
                }
            }
            else if (instrumentIndex == quoteFarIndex)
            {
                if (amount < 0)
                {
                    BuyNear(-amount, "ON_LIMIT_BV");
                }
                else if (amount > 0)
                {
                    SellNear(amount, "ON_LIMIT_BV");
                }
            }
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
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

                if (deal.isBuy && deal.source != "LIMIT_BV")
                {
                    pendingBuys -= deal.amount;
                }
                else
                {
                    pendingSells -= deal.amount;
                }

                cashflow += amount * deal.price * -1;

                CheckPending(instrumentIndex);

                if (deal.source != "LIMIT_BV")
                    return;

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