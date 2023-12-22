using System;
using System.Collections.Generic;
using KGClasses;
using StrategyLib;
using System.Timers;
using Detail;
using System.Reflection;
using System.Xml;
using System.Diagnostics;

namespace StrategyRunner
{
    public class LimitBV : Strategy
    {
        private BVConfig config;

        public double BVBuyPrice;
        public double BVSellPrice; //POTENTIALLY BVSellPrice < BVBuyPrice IF THERE'S ARBITRAGE
        public int BVBuyIndex;
        public int BVSellIndex;

        public double limitBVSellPrice;
        public double limitBVBuyPrice;
        public int limitBVBuyIndex;
        public int limitBVSellIndex;

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
        public int[] bfHolding;

        VolumeDetector.VolumeDetector bfDetector;
        Timer bfTimeout;

        Timer hedgeTimeout;
        bool shouldHedge = true;

        Hedging hedging;

        public double bvThrottleSeconds = -1;
        public int bvThrottleVolume = -1;
        public double creditOffset = -1;
        public int maxCrossVolume = -1;
        public int maxOutrights = -1;
        public int maxPosNear = -1;
        public int minPosNear = -1;
        public int maxPosFar = -1;
        public int minPosFar = -1;
        public double bvTimeoutSeconds = -1;
        public double bvMaxLoss = -1;

        public int bfTriggerVolume = -1;
        public int bfTriggerTradeCount = -1;
        public double bfTriggerSeconds = -1;
        public double bfTimeoutSeconds = -1;

        public string logLevel = "info";

        public bool bf = false;
        public double bfPrice = -11;
        public double entryPrice = -11;

        public LimitBV(API api, BVConfig config)
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
                bfHolding = new int[numAllInstruments];

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

                double bfTriggerMs = GetBFTriggerSeconds() * 1000;
                TimeSpan tBf = new TimeSpan(0, 0, 0, 0, (int)bfTriggerMs);
                bfDetector = new VolumeDetector.VolumeDetector(GetBFTriggerVolume(), GetBFTriggerTradeCount(), tBf);

                bfTimeout = new Timer();
                bfTimeout.Elapsed += OnBfTimeout;
                bfTimeout.AutoReset = false;

                hedgeTimeout = new Timer();
                hedgeTimeout.Elapsed += OnHedgeTimeout;
                hedgeTimeout.AutoReset = false;

                pendingResubmissions = new Dictionary<int, int>();

                double ms = GetEurexThrottleSeconds() * 1000;
                TimeSpan t = new TimeSpan(0, 0, 0, 0, (int)ms);
                eurexThrottler = new Throttler.EurexThrottler(GetEurexThrottleVolume(), t);

                orders = new Orders(this);
                hedging = new Hedging(this);

                API.Log("-->Start strategy:");
                API.StartStrategy(ref stgID, strategyOrders, instruments, 1, 5);

                API.Log("<--strategy");
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        private int GetBFTriggerVolume()
        {
            if (bfTriggerVolume == -1)
                return P.bfTriggerVolume;
            return bfTriggerVolume;
        }

        private int GetBFTriggerTradeCount()
        {
            if (bfTriggerTradeCount == -1)
                return P.bfTriggerTradeCount;
            return bfTriggerTradeCount;
        }

        private double GetBFTriggerSeconds()
        {
            if (bfTriggerSeconds == -1)
                return P.bfTriggerSeconds;
            return bfTriggerSeconds;
        }
        private double GetBFTimeoutSeconds()
        {
            if (bfTimeoutSeconds == -1)
                return P.bfTimeoutSeconds;
            return bfTimeoutSeconds;
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

        private int GetMaxCrossVolume()
        {
            if (maxCrossVolume == -1)
                return P.maxCrossVolume;
            return maxCrossVolume;
        }

        private int GetMaxOutrights()
        {
            if (maxOutrights == -1)
                return P.maxOutrights;
            return maxOutrights;
        }

        private int GetMaxPosNear()
        {
            if (maxPosNear == -1)
                return P.maxPosNear;
            return maxPosNear;
        }
        private int GetMaxPosFar()
        {
            if (maxPosFar == -1)
                return P.maxPosFar;
            return maxPosFar;
        }
        private int GetMinPosNear()
        {
            if (minPosNear == -1)
                return P.minPosNear;
            return minPosNear;
        }
        private int GetMinPosFar()
        {
            if (minPosFar == -1)
                return P.minPosFar;
            return minPosFar;
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

        public override string GetLogLevel()
        {
            return logLevel;
        }

        private void Log(string message)
        {
            API.Log(String.Format("STG {0}: {1}", stgID, message));
            API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        void LogDebug(string message)
        {
            if (GetLogLevel() == "debug")
                API.Log(String.Format("STG {0}: [LIMIT_BV] {1}", stgID, message));
        }

        public override void OnStatusChanged(int status)
        {
            if (status == 0)
            {
                API.CancelAllOrders(stgID);
            }
            if (status == 1)
            {
                shouldHedge = true;
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

        public override void ReloadConfig(Config c)
        {

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
        }

        private void CancelStrategy(string reason)
        {
            API.SetStrategyStatus(stgID, 0);
            API.CancelAllOrders(stgID);
            API.SendAlertBeep();
            API.Log(String.Format("CANCEL STG {0}: {1}", stgID, reason));
            API.SendToRemote(String.Format("CANCEL STG {0}: {1}", stgID, reason), KGConstants.EVENT_ERROR);
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
            if (BVPrice - API.GetImprovedCM(leanIndex) < boxTargetPrice - GetCreditOffset())
                return true;
            else
                return false || bf;
        }
        private bool isSellPreferred(double BVPrice)
        {
            if (BVPrice - API.GetImprovedCM(leanIndex) > boxTargetPrice + GetCreditOffset())
                return true;
            else
                return false || bf;
        }

        private void HedgeLeftovers()
        {
            foreach (var deal in pendingOrders)
            {
                orders.CancelOrder(deal.Key);
            }

            shouldHedge = true;
        }

        private int OnBFFinished()
        {
            bf = false;

            int positionTransferred = 0;
            for (int i = 0; i < numAllInstruments; i++)
            {
                holding[i] += bfHolding[i];
                positionTransferred += bfHolding[i];
                bfHolding[i] = 0;
            }

            shouldHedge = true;

            return positionTransferred;
        }

        private void OnBfTimeout(object sender, ElapsedEventArgs e)
        {
            try
            {
                int positionTransferred = OnBFFinished();
                Log(String.Format("BF finished, reason: timeout. BF position={0} transferred", positionTransferred));
            }
            catch (Exception ex)
            {
                Log(ex.Message);
            }
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

        private void SendLimitOrders()
        {
            try
            {
                int ordId;
                int qty = 0;
                double theBVPrice = Math.Max(bids[farIndex].price, bids[quoteIndex].price);
                if (isBuyPreferred(theBVPrice) && (holding[leanIndex] > -GetMaxOutrights()))
                {
                    if (bids[farIndex].price <= bids[quoteIndex].price)
                    {
                        if (((holding[farIndex] < GetMaxPosFar()) | (farIndex == quoteIndex)) && (!orders.orderInUse(limitBuy)))
                        {
                            double tickSize = API.GetTickSize(farIndex);
                            limitBVBuyPrice = Math.Round((theBVPrice -(tickSize / 2 - 1e-9)) / tickSize) * tickSize;
                            limitBVBuyIndex = farIndex;
                            limitBVSellIndex = quoteIndex;
                            qty = Math.Min(bids[quoteIndex].qty / 2, GetMaxCrossVolume());
                        }
                    }
                    if (bids[farIndex].price >= bids[quoteIndex].price)
                    {
                        if (((holding[quoteIndex] < GetMaxPosNear()) | (farIndex == quoteIndex)) && (!orders.orderInUse(limitBuy)))
                        {
                            if ((bids[farIndex].price > bids[quoteIndex].price) || (bids[farIndex].qty > bids[quoteIndex].qty))
                            {
                                double tickSize = API.GetTickSize(quoteIndex);
                                limitBVBuyPrice = Math.Round((theBVPrice - (tickSize / 2 - 1e-9)) / tickSize) * tickSize;
                                limitBVBuyIndex = quoteIndex;
                                limitBVSellIndex = farIndex;
                                qty = Math.Min(bids[farIndex].qty / 2, GetMaxCrossVolume());
                            }
                        }
                    }
                    if (qty > 0)
                    {
                        if (bf && limitBVBuyPrice > bfPrice + 1e-7)
                        {
                            return;
                        }
                        ordId = orders.SendOrder(limitBuy, limitBVBuyIndex, Side.BUY, limitBVBuyPrice, qty, "LIMIT_BV");
                    }
                }
                else
                    if (orders.orderInUse(limitBuy))
                        orders.CancelOrder(limitBuy);


                qty = 0;
                theBVPrice = Math.Min(asks[farIndex].price, asks[quoteIndex].price);
                if (isSellPreferred(theBVPrice) && (holding[leanIndex] < GetMaxOutrights()))
                {
                    if (asks[farIndex].price >= asks[quoteIndex].price)
                    {
                        if (((holding[farIndex] > GetMinPosFar()) | (farIndex == quoteIndex)) && (!orders.orderInUse(limitSell)))
                        {
                            double tickSize = API.GetTickSize(farIndex);
                            limitBVSellPrice = Math.Round((theBVPrice + (tickSize / 2 - 1e-9)) / tickSize) * tickSize;
                            limitBVSellIndex = farIndex;
                            limitBVBuyIndex = quoteIndex;
                            qty = Math.Min(asks[quoteIndex].qty / 2, GetMaxCrossVolume());
                        }
                    }
                    if (asks[farIndex].price <= asks[quoteIndex].price)
                    {
                        if (((holding[quoteIndex] > GetMinPosNear()) | (farIndex == quoteIndex)) && (!orders.orderInUse(limitSell)))
                        {
                            if ((asks[farIndex].price < asks[quoteIndex].price) || (asks[farIndex].qty > asks[quoteIndex].qty))
                            {
                                double tickSize = API.GetTickSize(quoteIndex);
                                limitBVSellPrice = Math.Round((theBVPrice + (tickSize / 2 - 1e-9)) / tickSize) * tickSize;
                                limitBVSellIndex = quoteIndex;
                                limitBVBuyIndex = farIndex;
                                qty = Math.Min(asks[farIndex].qty / 2, GetMaxCrossVolume());
                            }
                        }
                    }
                    if (qty > 0)
                    {
                        if (bf && limitBVBuyPrice > bfPrice + 1e-7)
                        {
                            return;
                        }
                        ordId = orders.SendOrder(limitSell, limitBVSellIndex, Side.SELL, limitBVSellPrice, qty, "LIMIT_BV");
                    }
                }
                else
                    if (orders.orderInUse(limitSell))
                        orders.CancelOrder(limitSell);
            }
            catch (Exception e)
            {
                API.Log("ERR in SendLimitOrders: " + e.ToString() + "," + e.StackTrace);

            }
        }

        private void CancelOnPriceMove(int instrumentIndex)
        {
            if ((!pricesAreEqual(limitBVBuyPrice, Math.Max(bids[quoteIndex].price, bids[farIndex].price))) && (orders.orderInUse(limitBuy)))
            {
                LogDebug(String.Format("CancelOnPriceMove limitBuy, instrument={0}", instrumentIndex));
                orders.CancelOrder(limitBuy);
            }
            else if ((!pricesAreEqual(limitBVSellPrice, Math.Min(asks[quoteIndex].price, asks[farIndex].price))) && (orders.orderInUse(limitSell)))
            {
                LogDebug(String.Format("CancelOnPriceMove limitSell, instrument={0}", instrumentIndex));
                orders.CancelOrder(limitSell);
            }
        }

        private bool shouldSellAtBid(double price, int instrumentIndex)
        {
            return pricesAreEqual(price, bids[instrumentIndex].price) && isSellPreferred(price) && !isBuyPreferred(price);
        }
        private bool shouldBuyAtAsk(double price, int instrumentIndex)
        {
            return pricesAreEqual(price, asks[instrumentIndex].price) && !isSellPreferred(price) && isBuyPreferred(price);
        }

        private void BF(double price, int instrumentIndex)
        {
            //TODO: on bf timeout, cancel the bf_init order if still active

            if (shouldSellAtBid(price, instrumentIndex))
            {
                if (GetNetPosition() != 0 && entryPrice != price)
                {
                    for (int i = 0; i < holding.Length; i++)
                    {
                        bfHolding[i] = holding[i];
                        holding[i] = 0;
                    }
                }
                var quantity = GetMaxCrossVolume() + GetNetPosition();
                Log(String.Format("BF_INIT SELL instrument={0} price={1} quantity={2}", instrumentIndex, price, quantity));
                orders.SendOrder(sell, instrumentIndex, Side.SELL, price, quantity, "BF_INIT");
                bf = true;
                bfPrice = price;
                bfTimeout.Start();
            }
            else if (shouldBuyAtAsk(price, instrumentIndex))
            {
                if (GetNetPosition() != 0 && entryPrice != price)
                {
                    for (int i = 0; i < holding.Length; i++)
                    {
                        bfHolding[i] = holding[i];
                        holding[i] = 0;
                    }
                }
                var quantity = GetMaxCrossVolume() + GetNetPosition();
                Log(String.Format("BF_INIT BUY instrument={0} price={1} quantity={2}", instrumentIndex, price, quantity));
                orders.SendOrder(buy, instrumentIndex, Side.BUY, price, quantity, "BF_INIT");
                bf = true;
                bfPrice = price;
                bfTimeout.Start();
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

                bool bfTrigger = false;
                double price = 0.0; 
                if (instrumentIndex == quoteIndex)
                {
                    int previousVolume = marketVolumeTraded[instrumentIndex];
                    int currentVolume = API.GetVolume(vit);
                    marketVolumeTraded[instrumentIndex] = currentVolume;
                    int lastTradeVolume = currentVolume - previousVolume;
                    price = API.GetLast(vi).price;
                    bfTrigger = bfDetector.addTrade(lastTradeVolume, price);

                    if (bf)
                    {
                        if (!pricesAreEqual(price, bfPrice))
                        {
                            bfTimeout.Stop();
                            int positionTransferred = OnBFFinished();
                            Log(String.Format("BF finished, reason: market deal with price={0} different from bf_price={1}. BF position={2} transferred", price, bfPrice, positionTransferred));
                        }
                    }
                }

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

                if (GetNetPosition() != 0)
                {
                    if (shouldHedge)
                        hedging.Hedge();
                    return;
                }

                if (!bf && bfTrigger)
                {
                    BF(price, instrumentIndex);
                }

                SendLimitOrders();
                CancelOnPriceMove(instrumentIndex);
            }
            catch (Exception e)
            {
                API.Log("ERR: " + e.ToString() + "," + e.StackTrace);
            }
        }

        public override void OnParamsUpdate(string paramName, string paramValue)
        {
            SetValue(this, paramName, paramValue);
        }

        public override void OnGlobalParamsUpdate()
        {
            bvThrottler.updateMaxVolume(GetBvThrottleVolume());
            bvThrottler.updateTimespan(GetBvThrottleSeconds());
            bfDetector.updateTriggerVolume(GetBFTriggerVolume());
            bfDetector.updateTriggerTradeCount(GetBFTriggerTradeCount());
            bfDetector.updateTimespan(GetBFTriggerSeconds());

            eurexThrottler.updateMaxVolume(GetEurexThrottleVolume());
            eurexThrottler.updateTimespan(GetEurexThrottleSeconds());

            double bvTimeoutSeconds = GetBvTimeoutSeconds();
            if (bvTimeoutSeconds > 0)
            {
                hedgeTimeout.Interval = bvTimeoutSeconds * 1000;
            }

            double bfTimeoutSeconds = GetBFTimeoutSeconds();
            if (bfTimeoutSeconds > 0)
            {
                bfTimeout.Interval = bfTimeoutSeconds * 1000;
            }
        }

        private void ExecuteOtherLegAtMarket(int instrumentIndex, int amount)
        {
            hedgeTimeout.Stop();
            hedgeTimeout.Start();
            shouldHedge = false;
            if (amount < 0)
            {
                bool cancelOK = orders.CancelOrder(limitSell);
                int orderId = Buy(-amount, "ON_LIMIT_BV", limitBVBuyIndex, limitBVSellPrice);
                if (orderId == -1)
                {
                    pendingResubmissions[buy.internalOrderNumber] = amount;
                }
            }
            else if (amount > 0)
            {
                bool cancelOK = orders.CancelOrder(limitBuy);
                int orderId = Sell(amount, "ON_LIMIT_BV", limitBVSellIndex, limitBVBuyPrice);
                if (orderId == -1)
                {
                    pendingResubmissions[sell.internalOrderNumber] = amount;
                }
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

                int positionBefore = GetNetPosition();

                if (deal.source != "BF_INIT")
                {
                    holding[instrumentIndex] += amount;

                    if (deal.source == "HEDGE" || deal.source == "INTERNAL")
                    {
                        hedging.Hedge();
                    }
                }
                else
                {
                    bfHolding[instrumentIndex] += amount;
                }

                int positionAfter = GetNetPosition();

                API.Log(String.Format("STG {0}: OnDeal instrument={1} order_id={2} source={3} amount={4} position={5}", stgID, instrumentIndex, deal.internalOrderNumber, deal.source, amount, positionAfter));

                if (positionBefore == 0 && positionAfter != 0)
                {
                    entryPrice = deal.price;
                }

                if (bf)
                {
                    bfTimeout.Stop();

                    if (pricesAreEqual(deal.price, bfPrice))
                    {
                        bfTimeout.Start();
                    }
                    else
                    {
                        int positionTransferred = OnBFFinished();
                        Log(String.Format("BF finished, reason: deal with price={0} different from bf_price={1}. BF position={2} transferred", deal.price, bfPrice, positionTransferred));
                    }
                }

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

            if (pendingResubmissions.ContainsKey(ord.internalOrderNumber))
            {
                int size = pendingResubmissions[ord.internalOrderNumber];
                pendingResubmissions.Remove(ord.internalOrderNumber);

                if (size < 0)
                {
                    Buy(-size, "RETRY", ord.index, ord.ask);
                }
                else if (size > 0)
                {
                    Sell(size, "RETRY", ord.index, ord.bid);
                }
            }
        }

        public static (bool, string) SetValue(object instance, string paramName, string paramValue)
        {
            string ret = "";
            bool found = false;
            bool valueChanged = false;
            foreach (FieldInfo field in instance.GetType().GetFields())
            {
                if (field.Name != paramName)
                    continue;
                else
                {
                    found = true;
                    if (field.FieldType == typeof(int))
                    {
                        int val = Int32.Parse(paramValue);
                        valueChanged = val != (int)field.GetValue(instance);
                        field.SetValue(instance, val);
                    }
                    else if (field.FieldType == typeof(string))
                    {
                        valueChanged = paramValue != (string)field.GetValue(instance);
                        field.SetValue(instance, paramValue);
                    }
                    else if (field.FieldType == typeof(double))
                    {
                        double val = Double.Parse(paramValue);
                        valueChanged = val != (double)field.GetValue(instance);
                        field.SetValue(instance, val);
                    }
                    else if (field.FieldType == typeof(bool))
                    {
                        if (paramValue == "true")
                        {
                            valueChanged = !(bool)field.GetValue(instance);
                            field.SetValue(instance, true);
                        }
                        else
                        {
                            valueChanged = (bool)field.GetValue(instance);
                            field.SetValue(instance, false);
                        }
                    }
                    else if (field.FieldType == typeof(long))
                    {
                        long val = long.Parse(paramValue);
                        valueChanged = val != (long)field.GetValue(instance);
                        field.SetValue(instance, val);
                    }
                    break;
                }
            }

            return (valueChanged, ret);
        }
    }
}