using System;
using System.Collections.Generic;
using KGClasses;
using StrategyLib;
using System.Timers;
using Detail;
using System.Xml;

namespace Config
{
    class BVConfig
    {
        public string nearInstrument;
        public string farInstrument;
        public string leanInstrument;

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
        private Config.BVConfig config;

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

        KGOrder limitBuy; //TODO: if we decide to send limit orders, initialize these and add them to strategyorders
        KGOrder limitSell;

        KGOrder limitBuyFar;
        KGOrder limitSellFar;

        List<VI> instruments;

        Throttler.Throttler bvThrottler;

        int pendingBuys = 0;
        int pendingSells = 0;

        Timer timeout;

        Hedging hedging;

        double positionValue;
        double cashflow;
        double pnl;

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
                theos = new double[numAllInstruments];

                for (int i = 0; i < numAllInstruments; i++)
                {
                    holding[i] = 0;
                    bids[i] = new DepthElement(-11, 0);
                    asks[i] = new DepthElement(11111, 0);
                    theos[i] = -11;
                }

                config = new Config.BVConfig(configFile, api);

                instruments = new List<VI>();

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

                TimeSpan tBv = new TimeSpan(0, 0, 0, 0, P.bvThrottleSeconds * 1000);

                bvThrottler = new Throttler.Throttler(P.bvThrottleVolume, tBv);

                timeout = new Timer();
                timeout.Elapsed += OnTimeout;
                timeout.AutoReset = false;

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

        private void SellNear(int n)
        {
            orders.SendOrder(sell, quoteIndex, Side.SELL, bids[quoteIndex].price, n, "BV");
        }

        private void BuyNear(int n)
        {
            orders.SendOrder(buy, quoteIndex, Side.BUY, asks[quoteIndex].price, n, "BV");
        }

        private void SellFar(int n)
        {
            orders.SendOrder(sellFar, quoteFarIndex, Side.SELL, bids[quoteFarIndex].price, n, "BV");
        }

        private void BuyFar(int n)
        {
            orders.SendOrder(buyFar, quoteFarIndex, Side.BUY, asks[quoteFarIndex].price, n, "BV");
        }

        private void HedgeLeftovers()
        {
            hedging.Hedge(pendingBuys - pendingSells, Source.NEAR);
            pendingBuys = 0;
            pendingSells = 0;
            cashflow = 0;
        }

        private void OnTimeout(object sender, ElapsedEventArgs e)
        {
            HedgeLeftovers();
        }

        private void TakeCross()
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

                SellNear(volume);
                BuyFar(volume);

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

                BuyNear(volume);
                SellFar(volume);

                pendingBuys += volume;
                pendingSells += volume;
            }
        }

        private double GetVwap(DepthElement bid, DepthElement ask)
        {
            if (bid.qty == 0 || ask.qty == 0)
            {
                return -11;
            }

            return (bid.price * ask.qty + ask.price * bid.qty) / (bid.qty + ask.qty);
        }

        private void CancelOnPriceMove(double theo)
        {
            if (orders.orderInUse(limitBuy))
            {
                if (limitBuy.bid - theo > 0.001)
                {
                    orders.CancelOrder(limitBuy);
                }
            }

            if (orders.orderInUse(limitSell))
            {
                if (theo - limitSell.ask > 0.001)
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
                double theo = GetVwap(bids[instrumentIndex], asks[instrumentIndex]);
                theos[instrumentIndex] = theo; //TODO: temporary, remove once theos in system

                positionValue = (pendingBuys - pendingSells) * theo;
                pnl = cashflow + positionValue;

                //CancelOnPriceMove(theo);

                if (pnl < P.bvMaxLoss)
                {
                    HedgeLeftovers();
                }

                if (!API.PassedTradeStart())
                {
                    return;
                }

                if (API.GetStrategyStatus(stgID) == 0 || systemTradingMode == 'C')
                {
                    return;
                }

                if (P.bvEnabled)
                {
                    TakeCross();
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

        public override void OnImprovedCM(int index, double CMPrice)
        {
            theos[index] = CMPrice;
        }

        public override void OnDeal(KGDeal deal)
        {
            try
            {
                int amount = deal.isBuy ? deal.amount : -deal.amount;

                int instrumentIndex = deal.index + API.n * deal.VenueID;

                holding[instrumentIndex] += amount;

                if (deal.isBuy)
                {
                    pendingBuys -= deal.amount;
                }
                else
                {
                    pendingSells -= deal.amount;
                }

                cashflow += amount * deal.price * -1;
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