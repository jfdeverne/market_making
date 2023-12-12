using Detail;
using KGClasses;
using StrategyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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
            mStrategy.API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        double GetLimitPlusBuyPrice(int index)
        {
            return mStrategy.asks[index].price - mStrategy.API.GetTickSize(index);
        }

        double GetLimitPlusSellPrice(int index)
        {
            return mStrategy.bids[index].price + mStrategy.API.GetTickSize(index);
        }

        private int GetHedgeInstrument(int quantity)
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
            }

            return hedgeIndex;
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

        private int Buy(int n, int index)
        {
            var order = buyOrders[index];
            int orderId = mOrders.SendOrder(order, index, Side.BUY, mStrategy.asks[index].price, n, "HEDGE", true);
            return orderId;
        }

        private int Sell(int n, int index)
        {
            var order = sellOrders[index];
            int orderId = mOrders.SendOrder(order, index, Side.SELL, mStrategy.bids[index].price, n, "HEDGE", true);
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

        public bool ShouldSellHedgeNow(int hedgeInstrument)
        {
            double price = mStrategy.bids[hedgeInstrument].price;
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

        public bool ShouldBuyHedgeNow(int hedgeInstrument)
        {
            double price = mStrategy.asks[hedgeInstrument].price;
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
            int hedgeInstrument = GetHedgeInstrument(-netPosition);

            if (netPosition > 0)
                shouldHedgeNow = ShouldSellHedgeNow(hedgeInstrument);
            else if (netPosition < 0)
                shouldHedgeNow = ShouldBuyHedgeNow(hedgeInstrument);

            if (shouldHedgeNow)
            {
                CancelAllHedgeOrders(hedgeInstrument);
                bool success = HedgeNow(-netPosition, hedgeInstrument);
                return success;
            }

            return false;
        }

        public void Hedge()
        {
            int netPosition = mStrategy.GetNetPosition();

            if (netPosition == 0)
            {
                CancelAllHedgeOrders();
                return;
            }

            if (TakeLimitPlus(netPosition))
                return;

            if (netPosition > 0)
            {
                foreach (var perInstrument in sellOrders)
                {
                    var index = perInstrument.Key;
                    var order = perInstrument.Value;

                    if (mOrders.orderInTransientState(order))
                        return;

                    if (order.bidSize > 0 && order.askSize > 0)
                    {
                        throw new Exception(String.Format("STG {0}: hedging order {1} has both bid and ask size", mStrategy.stgID, order));
                    }

                    var price = GetLimitPlusSellPrice(index);

                    double theoThreshold = mStrategy.API.GetImprovedCM(index) - mStrategy.GetMaxLossLimitHedge();

                    if (mOrders.orderInUse(order) && order.ask <= theoThreshold - (mStrategy.GetMaxLossLimitHedge() / 2.0))
                    {
                        mOrders.CancelOrder(order);
                        continue;
                    }

                    if (order.askSize == netPosition && pricesAreEqual(order.ask, price))
                        continue;

                    if (price <= theoThreshold)
                        continue;

                    LimitPlusSell(netPosition, index);
                }
            }
            else if (netPosition < 0)
            {
                foreach (var perInstrument in buyOrders)
                {
                    var index = perInstrument.Key;
                    var order = perInstrument.Value;

                    if (mOrders.orderInTransientState(order))
                        return;

                    if (order.bidSize > 0 && order.askSize > 0)
                    {
                        throw new Exception(String.Format("STG {0}: hedging order {1} has both bid and ask size", mStrategy.stgID, order));
                    }

                    var price = GetLimitPlusBuyPrice(index);

                    double theoThreshold = mStrategy.API.GetImprovedCM(index) + mStrategy.GetMaxLossLimitHedge();

                    if (mOrders.orderInUse(order) && order.bid >= theoThreshold + (mStrategy.GetMaxLossLimitHedge() / 2.0))
                    {
                        mOrders.CancelOrder(order);
                        continue;
                    }

                    if (order.bidSize == -netPosition && pricesAreEqual(order.bid, price))
                        continue;

                    if (price >= theoThreshold)
                        continue;

                    LimitPlusBuy(-netPosition, index);
                }
            }
        }

        public bool HedgeNow(int quantity, int hedgeInstrument)
        {
            if (quantity == 0)
            {
                return true;
            }

            if (quantity < 0)
            {
                int orderId = Sell(-quantity, hedgeInstrument);
                if (orderId == -1)
                {
                    return true;
                }
                mIOCs.Add(orderId);
                return true;
            }
            else if (quantity > 0)
            {
                int orderId = Buy(quantity, hedgeInstrument);
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