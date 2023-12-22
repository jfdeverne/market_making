using Detail;
using KGClasses;
using StrategyLib;
using System;
using System.Collections.Generic;

namespace Detail
{
    public enum Hedge
    {
        NONE = 0,
        LIMITPLUS = 1,
        MARKET = 2
    }
}


namespace StrategyRunner
{
    public class Hedging
    {
        Strategy mStrategy;

        Dictionary<int, KGOrder> buyOrders;
        Dictionary<int, KGOrder> sellOrders;

        Orders mOrders;

        List<int> mIOCs;
        List<int> mIOCCancels;

        bool isHedging = false;

        public Hedging(Strategy strategy)
        {
            mStrategy = strategy;

            buyOrders = new Dictionary<int, KGOrder>();
            sellOrders = new Dictionary<int, KGOrder>();

            foreach (var index in strategy.correlatedIndices)
            {
                KGOrder buyOrder = new KGOrder();
                buyOrders[index] = buyOrder;
                KGOrder sellOrder = new KGOrder();
                sellOrders[index] = sellOrder;
                mStrategy.strategyOrders.Add(buyOrder);
                mStrategy.strategyOrders.Add(sellOrder);
            }

            foreach (var index in strategy.crossVenueIndices)
            {
                KGOrder buyOrder = new KGOrder();
                buyOrders[index] = buyOrder;
                KGOrder sellOrder = new KGOrder();
                sellOrders[index] = sellOrder;
                mStrategy.strategyOrders.Add(buyOrder);
                mStrategy.strategyOrders.Add(sellOrder);
            }

            mIOCs = new List<int>();
            mIOCCancels = new List<int>();

            mOrders = mStrategy.orders;
        }

        private void Log(string message)
        {
            mStrategy.API.Log(String.Format("STG {0}: {1}", mStrategy.stgID, message));
            //mStrategy.API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        void LogDebug(string message)
        {
            if (mStrategy.GetLogLevel() == "debug")
                mStrategy.API.Log(String.Format("STG {0}: [HEDGING] {1}", mStrategy.stgID, message));
        }

        double GetLimitPlusBuyPrice(int index)
        {
            return mStrategy.asks[index].price - mStrategy.API.GetTickSize(index);
        }

        double GetLimitPlusSellPrice(int index)
        {
            return mStrategy.bids[index].price + mStrategy.API.GetTickSize(index);
        }

        private (int, double) GetHedgeInstrument(int quantity)
        {
            int hedgeIndex = -1;
            double bestOffer = double.MaxValue;
            double bestBid = double.MinValue;
            int hedgerQty = 0;

            if (quantity > 0)
            {
                foreach (var index in mStrategy.crossVenueIndices)
                {
                    if (mStrategy.holding[index] + quantity < P.maxOutrights)
                    {
                        if ((mStrategy.asks[index].price < bestOffer) || ((mStrategy.asks[index].price == bestOffer) && (mStrategy.asks[index].qty > hedgerQty)))
                        {
                            bestOffer = mStrategy.asks[index].price;
                            hedgeIndex = index;
                            hedgerQty = mStrategy.asks[index].qty;
                        }
                    }
                }

                foreach (var index in mStrategy.correlatedIndices)
                {
                    if (mStrategy.holding[index] + quantity < P.maxOutrights)
                    {
                        if ((mStrategy.asks[index].price + mStrategy.correlatedSpreadTargetPrice < bestOffer) || ((mStrategy.asks[index].price + mStrategy.correlatedSpreadTargetPrice == bestOffer) && (mStrategy.asks[index].qty > hedgerQty)))
                        {
                            bestOffer = mStrategy.asks[index].price + mStrategy.correlatedSpreadTargetPrice;
                            hedgeIndex = index;
                            hedgerQty = mStrategy.asks[index].qty;
                        }
                    }
                }
                return (hedgeIndex, bestOffer);
            }
            else if (quantity < 0)
            {
                foreach (var index in mStrategy.crossVenueIndices)
                {
                    if (mStrategy.holding[index] + quantity > -P.maxOutrights)
                    {
                        if ((mStrategy.bids[index].price > bestBid) || ((mStrategy.bids[index].price == bestBid) && (mStrategy.bids[index].qty > hedgerQty)))
                        {
                            bestBid = mStrategy.bids[index].price;
                            hedgeIndex = index;
                            hedgerQty = mStrategy.bids[index].qty;
                        }
                    }
                }

                foreach (var index in mStrategy.correlatedIndices)
                {
                    if (mStrategy.holding[index] + quantity > -P.maxOutrights)
                    {
                        if ((mStrategy.bids[index].price + mStrategy.correlatedSpreadTargetPrice > bestBid) || ((mStrategy.bids[index].price + mStrategy.correlatedSpreadTargetPrice == bestBid) && (mStrategy.bids[index].qty > hedgerQty)))
                        {
                            bestBid = mStrategy.bids[index].price + mStrategy.correlatedSpreadTargetPrice;
                            hedgeIndex = index;
                            hedgerQty = mStrategy.bids[index].qty;
                        }
                    }
                }
                return (hedgeIndex, bestBid);
            }

            return (-1, -11);
        }

        public void CheckIOC()
        {
            foreach (var ord in mStrategy.strategyOrders)
            {
                if (!mOrders.orderInTransientState(ord) && ord.bidSize < 0 && ord.askSize < 0)
                {
                    if (mIOCs.Contains(ord.internalOrderNumber))
                    {
                        mIOCs.Remove(ord.internalOrderNumber);
                        Hedge();
                    }
                }
                else if (!mOrders.orderInTransientState(ord) && ord.orderStatus != 4)
                {
                    if (mIOCs.Contains(ord.internalOrderNumber))
                    {
                        mIOCs.Remove(ord.internalOrderNumber);
                        mIOCCancels.Add(ord.internalOrderNumber);
                        mOrders.CancelOnNextMD(ord);
                        Hedge();
                    }
                }
            }
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private void LimitPlusBuy(int n, int index)
        {
            var order = buyOrders[index];

            double price = GetLimitPlusBuyPrice(index);

            double icm = mStrategy.API.GetImprovedCM(index);

            if (icm - price < 0.0025)
            {

            }

            int orderId = mOrders.SendOrder(order, index, Side.BUY, price, n, "HEDGE", true);

            if (orderId == -1)
            {
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }
        }
        private void LimitPlusSell(int n, int index)
        {
            var order = sellOrders[index];

            double price = GetLimitPlusSellPrice(index);

            int orderId = mOrders.SendOrder(order, index, Side.SELL, price, n, "HEDGE", true);

            if (orderId == -1)
            {
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }
        }

        private int Buy(int n, int index, double price)
        {
            var order = buyOrders[index];
            int orderId = mOrders.SendOrder(order, index, Side.BUY, price, n, "HEDGE", true);
            return orderId;
        }

        private int Sell(int n, int index, double price)
        {
            var order = sellOrders[index];
            int orderId = mOrders.SendOrder(order, index, Side.SELL, price, n, "HEDGE", true);
            return orderId;
        }

        public void CancelAllHedgeOrders(int except = -1)
        {
            foreach (var perInstrument in buyOrders)
            {
                var order = perInstrument.Value;
                if (mOrders.orderInUse(order))
                {
                    if (perInstrument.Key != except)
                        mOrders.CancelOrder(order);
                }
            }

            foreach (var perInstrument in sellOrders)
            {
                var order = perInstrument.Value;
                if (mOrders.orderInUse(order))
                {
                    if (perInstrument.Key != except)
                        mOrders.CancelOrder(order);
                }
            }
        }

        public bool ShouldSellHedgeNow((int, double) hedgeInstrumentAndPrice)
        {
            int hedgeInstrument = hedgeInstrumentAndPrice.Item1;
            double price = hedgeInstrumentAndPrice.Item2;

            int limitPlusSize = mStrategy.limitPlusSize;
            if (hedgeInstrument != mStrategy.leanIndex)
                limitPlusSize = mStrategy.nonLeanLimitPlusSize;

            if (mStrategy.bids[hedgeInstrument].qty < limitPlusSize
                && mStrategy.bids[hedgeInstrument].price >= mStrategy.API.GetImprovedCM(hedgeInstrument) - mStrategy.GetMaxLossMarketHedge())
                return true;

            if (hedgeInstrument % mStrategy.API.n == mStrategy.quoteIndex % mStrategy.API.n) //SAME INSTRUMENT, POSSIBLY CROSS VENUE
            {
                if (price - mStrategy.API.GetImprovedCM(mStrategy.leanIndex) > mStrategy.boxTargetPrice)
                    return true;
            }
            else if (hedgeInstrument % mStrategy.API.n == mStrategy.leanIndex % mStrategy.API.n)
            {
                if (price - mStrategy.API.GetImprovedCM(mStrategy.leanIndex) > 0)
                    return true;
            }
            else //NO OTHER OPTIONS CURRNTLY
                return false;
            
            return false;
        }

        public bool ShouldBuyHedgeNow((int, double) hedgeInstrumentAndPrice)
        {
            int hedgeInstrument = hedgeInstrumentAndPrice.Item1;
            double price = hedgeInstrumentAndPrice.Item2;

            int limitPlusSize = mStrategy.limitPlusSize;
            if (hedgeInstrument != mStrategy.leanIndex)
                limitPlusSize = mStrategy.nonLeanLimitPlusSize;

            if (mStrategy.bids[hedgeInstrument].qty < limitPlusSize
                && mStrategy.asks[hedgeInstrument].price <= mStrategy.API.GetImprovedCM(hedgeInstrument) + mStrategy.GetMaxLossMarketHedge())
                return true;

            if (hedgeInstrument % mStrategy.API.n == mStrategy.quoteIndex % mStrategy.API.n) //SAME INSTRUMENT, POSSIBLY CROSS VENUE
            {
                if (price - mStrategy.API.GetImprovedCM(mStrategy.leanIndex) < mStrategy.boxTargetPrice)
                    return true;
            }
            else if (hedgeInstrument % mStrategy.API.n == mStrategy.leanIndex % mStrategy.API.n)
            {
                if (price - mStrategy.API.GetImprovedCM(mStrategy.leanIndex) < 0)
                    return true;
            }
            else //NO OTHER OPTIONS CURRNTLY
                return false;

            return false;
        }


        public bool TakeLimitPlus(int netPosition)
        {
            bool shouldHedgeNow = false;
            (int, double) hedgeInstrumentAndPrice = GetHedgeInstrument(-netPosition);

            if (netPosition > 0)
                shouldHedgeNow = ShouldSellHedgeNow(hedgeInstrumentAndPrice);
            else if (netPosition < 0)
                shouldHedgeNow = ShouldBuyHedgeNow(hedgeInstrumentAndPrice);

            if (shouldHedgeNow)
            {
                CancelAllHedgeOrders(hedgeInstrumentAndPrice.Item1);
                bool success = HedgeNow(-netPosition, hedgeInstrumentAndPrice);
                return success;
            }

            return false;
        }

        public (double, int) GetHedges(int position)
        {
            int frequency = 0;

            if (position > 0)
            {
                double bestPrice = 11111;

                foreach (var perInstrument in sellOrders)
                {
                    var index = perInstrument.Key;
                    var order = perInstrument.Value;

                    if (mOrders.orderInTransientState(order))
                    {
                        LogDebug(String.Format("GetHedges: skipping order for instrument={0} internal={1} number={2}: in transient state", index, order.internalOrderNumber, order.orderNumber));
                        continue;
                    }

                    var price = GetLimitPlusSellPrice(index);

                    if (mStrategy.correlatedIndices.Contains(index))
                    {
                        price += mStrategy.correlatedSpreadTargetPrice;
                    }

                    double theoThreshold = mStrategy.API.GetImprovedCM(index) - mStrategy.GetMaxLossLimitHedge();

                    double hysteresisThreshold = theoThreshold - (mStrategy.GetMaxLossLimitHedge() / 2.0);

                    if (mOrders.orderInUse(order) && order.ask <= hysteresisThreshold)
                    {
                        Log(String.Format("GetHedges: skipping order for instrument={0} internal={1} number={2}: ask={3} <= hysterisis_threshold={4}", index, order.internalOrderNumber, order.orderNumber, order.ask, hysteresisThreshold));
                        continue;
                    }

                    if (!mOrders.orderInUse(order) && price <= theoThreshold)
                    {
                        LogDebug(String.Format("GetHedges: skipping order for instrument={0}: price={1} <= theo_threshold={2}", index, price, theoThreshold));
                        continue;
                    }

                    if (price < bestPrice - 0.0026)
                    {
                        LogDebug(String.Format("GetHedges: instrument {0}: new best price {1}", index, price));
                        frequency = 1;
                        bestPrice = price;
                    }
                    else
                    {
                        LogDebug(String.Format("GetHedges: instrument {0}: frequency {1}", index, frequency));
                        frequency++;
                    }
                }

                return (bestPrice, frequency);
            }
            else if (position < 0)
            {
                double bestPrice = -11;

                foreach (var perInstrument in buyOrders)
                {
                    var index = perInstrument.Key;
                    var order = perInstrument.Value;

                    if (mOrders.orderInTransientState(order))
                    {
                        LogDebug(String.Format("GetHedges: skipping order for instrument={0} internal={1} number={2}: in transient state", index, order.internalOrderNumber, order.orderNumber));
                        continue;
                    }

                    var price = GetLimitPlusBuyPrice(index);

                    if (mStrategy.correlatedIndices.Contains(index))
                    {
                        price += mStrategy.correlatedSpreadTargetPrice;
                    }

                    double theoThreshold = mStrategy.API.GetImprovedCM(index) + mStrategy.GetMaxLossLimitHedge();

                    double hysteresisThreshold = theoThreshold + (mStrategy.GetMaxLossLimitHedge() / 2.0);

                    if (mOrders.orderInUse(order) && order.bid >= hysteresisThreshold)
                    {
                        Log(String.Format("GetHedges: skipping order for instrument={0} internal={1} number={2}: ask={3} >= hysterisis_threshold={4}", index, order.internalOrderNumber, order.orderNumber, order.ask, hysteresisThreshold));
                        continue;
                    }

                    if (!mOrders.orderInUse(order) && price >= theoThreshold)
                    {
                        LogDebug(String.Format("GetHedges: skipping order for instrument={0}: price={1} >= theo_threshold={2}", index, price, theoThreshold));
                        continue;
                    }

                    if (price > bestPrice + 0.0026)
                    {
                        LogDebug(String.Format("GetHedges: instrument {0}: new best price {1}", index, price));
                        frequency = 1;
                        bestPrice = price;
                    }
                    else
                    {
                        LogDebug(String.Format("GetHedges: instrument {0}: frequency {1}", index, frequency));
                        frequency++;
                    }
                }

                return (bestPrice, frequency);
            }

            return (-11, 0);
        }

        public void Hedge()
        {
            if (isHedging)
                return;
            isHedging = true;

            int netPosition = mStrategy.GetNetPosition();

            if (netPosition == 0)
            {
                CancelAllHedgeOrders();
                isHedging = false;
                return;
            }

            if (TakeLimitPlus(netPosition))
            {
                isHedging = false;
                return;
            }

            (double bestPrice, int frequency) = GetHedges(netPosition);
            if (frequency == 0)
            {
                Log("no viable hedge instruments");
                isHedging = false;
                return;
            }

            if (netPosition > 0)
            {
                Log(String.Format("hedging position {0}", netPosition));
                foreach (var perInstrument in sellOrders)
                {
                    var index = perInstrument.Key;
                    var order = perInstrument.Value;

                    if (mOrders.orderInTransientState(order))
                    {
                        LogDebug(String.Format("order instrument={0} internal={1} number={2} in transient state, continuing", index, order.internalOrderNumber, order.orderNumber));
                        continue;
                    }

                    if (order.bidSize > 0 && order.askSize > 0)
                    {
                        throw new Exception(String.Format("STG {0}: hedging order {1} has both bid and ask size", mStrategy.stgID, order));
                    }

                    var price = GetLimitPlusSellPrice(index);

                    var priceWithSpread = price;

                    if (mStrategy.correlatedIndices.Contains(index))
                    {
                        priceWithSpread += mStrategy.correlatedSpreadTargetPrice;
                    }

                    int quantity = netPosition;

                    if (priceWithSpread < bestPrice + 0.0026 && frequency > 1)
                    {
                        quantity = (int)(netPosition / (frequency * P.simultaneousHedgersTaperFactor));
                        Log(String.Format("tapering simultaneous hedgers, position={0} frequency={1} factor={2} final_qty={3}", netPosition, frequency, P.simultaneousHedgersTaperFactor, quantity));
                    }
                    else
                    {
                        Log(String.Format("NOT tapering, frequency={0} is one or price_with_spread={1} >= best_price_adjusted={2}, factor={3}", frequency, priceWithSpread, bestPrice + 0.0026, P.simultaneousHedgersTaperFactor));
                    }

                    if (quantity == 0)
                        quantity = 1;

                    double theoThreshold = mStrategy.API.GetImprovedCM(index) - mStrategy.GetMaxLossLimitHedge();

                    double hysterisisThreshold = theoThreshold - (mStrategy.GetMaxLossLimitHedge() / 2.0);

                    if (mOrders.orderInUse(order) && order.ask <= hysterisisThreshold)
                    {
                        Log(String.Format("cancelling order instrument={0} internal={1} number={2}: ask={3} <= hysterisis_threshold={4}", index, order.internalOrderNumber, order.orderNumber, order.ask, hysterisisThreshold));
                        mOrders.CancelOrder(order);
                        continue;
                    }

                    if (order.askSize == quantity && pricesAreEqual(order.ask, price))
                    {
                        LogDebug(String.Format("desired sell order instrument={0} internal={1} number={2} already working, continuing", index, order.internalOrderNumber, order.orderNumber));

                        if (price <= theoThreshold)
                        {
                            Log(String.Format("working order instrument={0} internal={1} number={2}, price={3} <= theo_threshold={4} but not < hysterisis_threshold={5}, no action required", index, order.internalOrderNumber, order.orderNumber, price, theoThreshold, hysterisisThreshold));
                        }
                        continue;
                    }

                    if (!mOrders.orderInUse(order) && price <= theoThreshold)
                    {
                        LogDebug(String.Format("order for instrument={0} will not be submitted, price={1} <= theo_threshold={2}", index, price, theoThreshold));
                        continue;
                    }

                    LimitPlusSell(quantity, index);
                }
            }
            else if (netPosition < 0)
            {
                Log(String.Format("hedging position {0}", netPosition));
                foreach (var perInstrument in buyOrders)
                {
                    var index = perInstrument.Key;
                    var order = perInstrument.Value;

                    if (mOrders.orderInTransientState(order))
                    {
                        LogDebug(String.Format("order instrument={0} internal={1} number={2} in transient state, continuing", index, order.internalOrderNumber, order.orderNumber));
                        continue;
                    }

                    if (order.bidSize > 0 && order.askSize > 0)
                    {
                        throw new Exception(String.Format("STG {0}: hedging order {1} has both bid and ask size", mStrategy.stgID, order));
                    }

                    var price = GetLimitPlusBuyPrice(index);

                    var priceWithSpread = price;

                    if (mStrategy.correlatedIndices.Contains(index))
                    {
                        priceWithSpread += mStrategy.correlatedSpreadTargetPrice;
                    }

                    int quantity = netPosition;

                    if (priceWithSpread > bestPrice - 0.0026 && frequency > 1)
                    {
                        quantity = (int)(netPosition / (frequency * P.simultaneousHedgersTaperFactor));
                        Log(String.Format("tapering simultaneous hedgers, position={0} frequency={1} factor={2} final_qty={3}", netPosition, frequency, P.simultaneousHedgersTaperFactor, quantity));
                    }
                    else
                    {
                        Log(String.Format("NOT tapering, frequency={0} is one or price_with_spread={1} <= best_price_adjusted={2}, factor={3}", frequency, priceWithSpread, bestPrice + 0.0026, P.simultaneousHedgersTaperFactor));
                    }

                    if (quantity == 0)
                        quantity = 1;

                    double theoThreshold = mStrategy.API.GetImprovedCM(index) + mStrategy.GetMaxLossLimitHedge();

                    double hysterisisThrehsold = theoThreshold + (mStrategy.GetMaxLossLimitHedge() / 2.0);

                    if (mOrders.orderInUse(order) && order.bid >= hysterisisThrehsold)
                    {
                        Log(String.Format("cancelling order instrument={0} internal={1} number={2}: bid={3} >= hysterisis_threshold={4}", index, order.internalOrderNumber, order.orderNumber, order.bid, hysterisisThrehsold));
                        mOrders.CancelOrder(order);
                        continue;
                    }

                    if (order.bidSize == -quantity && pricesAreEqual(order.bid, price))
                    {
                        LogDebug(String.Format("desired buy order instrument={0} internal={1} number={2} already working, continuing", index, order.internalOrderNumber, order.orderNumber));

                        if (price >= theoThreshold)
                        {
                            Log(String.Format("working order instrument={0} internal={1} number={2}, price={3} >= theo_threshold={4} but not > hysteririsis_threshold={5}, no action required", index, order.internalOrderNumber, order.orderNumber, price, theoThreshold, hysterisisThrehsold));
                        }
                        continue;
                    }

                    if (!mOrders.orderInUse(order) && price >= theoThreshold)
                    {
                        LogDebug(String.Format("order for instrument={0} will not be submitted, price={1} >= theo_threshold={2}", index, price, theoThreshold));
                        continue;
                    }

                    LimitPlusBuy(-quantity, index);
                }
            }
            isHedging = false;
        }
        public bool HedgeNow(int quantity, (int, double) hedgeInstrumentAndPrice)
        {
            int hedgeInstrument = hedgeInstrumentAndPrice.Item1;
            double price = hedgeInstrumentAndPrice.Item2;

            if (quantity == 0)
            {
                return true;
            }

            if (quantity < 0)
            {
                int orderId = Sell(-quantity, hedgeInstrument, price);
                if (orderId == -1)
                {
                    return true;
                }
                mIOCs.Add(orderId);
                return true;
            }
            else if (quantity > 0)
            {
                int orderId = Buy(quantity, hedgeInstrument, price);
                if (orderId == -1)
                {
                    return true;
                }
                mIOCs.Add(orderId);
                return true;
            }

            return true;
        }
    }
}