using Detail;
using KGClasses;
using System;
using System.Collections.Generic;

namespace Detail
{
    class StopInfo
    {
        public bool inUse = false;
        public int instrument;
        public double stopPrice;
        public int stopVolume;
        public int leanInstrument;
        public int parentOrderId;
        public int volume;
        public Side side;
    }

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

        KGOrder order;

        Detail.StopInfo stopInfo;

        Orders mOrders;

        List<int> mIOCs;
        List<int> mIOCCancels;

        public Hedging(Strategy strategy)
        {
            mStrategy = strategy;

            order = new KGOrder();

            mStrategy.strategyOrders.Add(order);

            stopInfo = new StopInfo();

            mIOCs = new List<int>();
            mIOCCancels = new List<int>();

            mOrders = mStrategy.orders;
        }

        private void Log(string message)
        {
            mStrategy.API.Log(String.Format("STG {0}: {1}", mStrategy.stgID, message));
            mStrategy.API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        private int GetHedgeInstrument(int quantity) //TODO: rework
        {
            int hedgeIndex = -1;
            double bestOffer = double.MaxValue;
            double bestBid = double.MinValue;

            if (quantity > 0)
            {
                if (mStrategy.holding[mStrategy.quoteIndex] + quantity < P.maxOutrights)
                    if (mStrategy.asks[mStrategy.quoteIndex].price < bestOffer)
                    {
                        bestOffer = mStrategy.asks[mStrategy.quoteIndex].price;
                        hedgeIndex = mStrategy.quoteIndex;
                    }
                if (mStrategy.holding[mStrategy.farIndex] + quantity < P.maxOutrights)
                    if (mStrategy.asks[mStrategy.farIndex].price < bestOffer)
                    {
                        bestOffer = mStrategy.asks[mStrategy.farIndex].price;
                        hedgeIndex = mStrategy.farIndex;
                    }
                if (mStrategy.holding[mStrategy.leanIndex] + quantity < P.maxOutrights)
                    if (mStrategy.asks[mStrategy.leanIndex].price + mStrategy.boxTargetPrice < bestOffer)
                    {
                        bestOffer = mStrategy.asks[mStrategy.leanIndex].price + mStrategy.boxTargetPrice;
                        hedgeIndex = mStrategy.leanIndex;
                    }
            }
            else if (quantity < 0)
            {
                if (mStrategy.holding[mStrategy.leanIndex] + quantity > -P.maxOutrights)
                    if (mStrategy.bids[mStrategy.leanIndex].price + mStrategy.boxTargetPrice > bestBid)
                    {
                        bestBid = mStrategy.bids[mStrategy.leanIndex].price + mStrategy.boxTargetPrice;
                        hedgeIndex = mStrategy.leanIndex;
                    }
                if (mStrategy.holding[mStrategy.quoteIndex] + quantity > -P.maxOutrights)
                    if (mStrategy.bids[mStrategy.quoteIndex].price > bestBid)
                    {
                        bestBid = mStrategy.bids[mStrategy.quoteIndex].price;
                        hedgeIndex = mStrategy.quoteIndex;
                    }
                if (mStrategy.holding[mStrategy.farIndex] + quantity > -P.maxOutrights)
                    if (mStrategy.bids[mStrategy.farIndex].price > bestBid)
                    {
                        bestBid = mStrategy.bids[mStrategy.farIndex].price;
                        hedgeIndex = mStrategy.farIndex;
                    }
                
            }

            return hedgeIndex;
        }

        private void PostStopOrder(int instrument, int leanInstrument, double limitPrice, double stopPrice, int volume, int stopVolume, Detail.Side side, int parentOrderId)
        {
            Log(String.Format("posting stop order for ins={0} lmt_price={1} stp_price={2} vol={3} stp_vol={4}", instrument, limitPrice, stopPrice, volume, stopVolume));
            
            stopInfo.inUse = true;
            stopInfo.instrument = instrument;
            stopInfo.stopPrice = stopPrice;
            stopInfo.stopVolume = stopVolume;
            stopInfo.leanInstrument = leanInstrument;
            stopInfo.parentOrderId = parentOrderId;
            stopInfo.volume = volume;
            stopInfo.side = side;

            mStrategy.activeStopOrders = true;
        }

        private Hedge shouldStopBuy(StopInfo info)
        {
            int index = info.leanInstrument != -1 ? info.leanInstrument : info.instrument;
            bool volumeAtStopPriceLow = (mStrategy.asks[index].price == info.stopPrice && mStrategy.asks[index].qty < info.stopVolume);
            bool marketRanAway = mStrategy.asks[index].price > info.stopPrice;
            bool theoRanAway = mStrategy.API.GetImprovedCM(index) > info.stopPrice - mStrategy.GetMaxLossMarketHedge();

            if (theoRanAway && volumeAtStopPriceLow)
            {
                return Detail.Hedge.MARKET;
            }
            else if (marketRanAway)
            {
                return Detail.Hedge.LIMITPLUS;
            }

            return Detail.Hedge.NONE;
        }

        private Hedge shouldStopSell(StopInfo info)
        {
            int index = info.leanInstrument != -1 ? info.leanInstrument : info.instrument;
            bool volumeAtStopPriceLow = (mStrategy.bids[index].price == info.stopPrice && mStrategy.bids[index].qty < info.stopVolume);
            bool marketRanAway = mStrategy.bids[index].price < info.stopPrice;
            bool theoRanAway = mStrategy.API.GetImprovedCM(index) < info.stopPrice + mStrategy.GetMaxLossMarketHedge();

            if (theoRanAway && volumeAtStopPriceLow)
            {
                return Detail.Hedge.MARKET;
            }
            else if (marketRanAway)
            {
                return Detail.Hedge.LIMITPLUS;
            }

            return Detail.Hedge.NONE;
        }

        private void OnStopTrigger(StopInfo stopInfo, Func<StopInfo, Detail.Hedge> shouldTrigger)
        {
            switch (shouldTrigger(stopInfo))
            {
                case Detail.Hedge.MARKET:
                    int orderId = HedgeNow(stopInfo.volume);
                    stopInfo.inUse = false;
                    mStrategy.activeStopOrders = false;
                    if (stopInfo.parentOrderId != -1)
                        mOrders.CancelOrder(stopInfo.parentOrderId);
                    break;
                case Detail.Hedge.LIMITPLUS:
                    stopInfo.inUse = false;
                    mStrategy.activeStopOrders = false;
                    if (stopInfo.parentOrderId != -1)
                        mOrders.CancelOrder(stopInfo.parentOrderId);
                    Hedge();
                    break;
                case Detail.Hedge.NONE:
                    break;
            }
        }

        public void EvaluateStops()
        {
            if (stopInfo.inUse)
            {
                switch (stopInfo.side)
                {
                    case Detail.Side.BUY:
                        OnStopTrigger(stopInfo, shouldStopSell);
                        break;
                    case Detail.Side.SELL:
                        OnStopTrigger(stopInfo, shouldStopBuy);
                        break;
                }
            }
        }

        public void PropagateToStopOrder(int parentOrderId)
        {
            if (parentOrderId == stopInfo.parentOrderId)
            {
                stopInfo.parentOrderId = -1;
                stopInfo.inUse = false;
            }

            if (mStrategy.activeStopOrders && !stopInfo.inUse)
            {
                mStrategy.activeStopOrders = false;
            }
        }

        public void OnOrder(KGOrder ord)
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

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private void LimitPlusBuy(int n, int index)
        {
            double price = mStrategy.asks[index].price - mStrategy.tickSize;

            if (!pricesAreEqual(order.ask, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, index, Side.BUY, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    return;
                }
            }

            PostStopOrder(index, mStrategy.leanIndex, price, mStrategy.asks[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.BUY, order.internalOrderNumber);
        }

        private void LimitPlusSell(int n, int index)
        {
            double price = mStrategy.bids[index].price + mStrategy.tickSize;

            if (!pricesAreEqual(order.ask, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, index, Side.SELL, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    return;
                }
            }

            PostStopOrder(index, mStrategy.leanIndex, price, mStrategy.bids[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.SELL, order.internalOrderNumber);
        }

        private int Buy(int n, int index)
        {
            int orderId = mOrders.SendOrder(order, index, Side.BUY, mStrategy.asks[index].price, n, "HEDGE");
            return orderId;
        }

        private int Sell(int n, int index)
        {
            int orderId = mOrders.SendOrder(order, index, Side.SELL, mStrategy.bids[index].price, n, "HEDGE");
            return orderId;
        }

        public void Hedge()
        {
            int netPosition = mStrategy.GetNetPosition();

            if (netPosition == 0)
            {
                mOrders.CancelOrder(order);
                return;
            }

            int pending = mOrders.orderInUse(order) && order.orderStatus != 41 ? order.bidSize : 0; //TODO: or ask size, whichever is > 0

            int quantity = netPosition + pending;

            if (quantity == 0)
                return;

            Log(String.Format("hedging holding={0}, qty={1}", netPosition, quantity));

            int hedgeInstrument = GetHedgeInstrument(quantity);

            if (quantity > 0)
            {
                LimitPlusSell(quantity, hedgeInstrument);
            }
            else if (quantity < 0)
            {
                LimitPlusBuy(-quantity, hedgeInstrument);
            }
        }

        public int HedgeNow(int quantity)
        {
            if (quantity == 0)
            {
                return -1;
            }

            int hedgeInstrument = GetHedgeInstrument(quantity);

            if (quantity < 0)
            {
                int orderId = Sell(-quantity, hedgeInstrument);
                mIOCs.Add(orderId);
                return orderId;
            }
            else if (quantity > 0)
            {
                int orderId = Buy(quantity, hedgeInstrument);
                mIOCs.Add(orderId);
                return orderId;
            }

            return -1;
        }
    }
}