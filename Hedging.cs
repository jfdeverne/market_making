using Detail;
using KGClasses;
using StrategyLib;
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

        public int parent1OrderId;
        public int parent2OrderId;

        public int volume;
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

        KGOrder leanBuy;
        KGOrder leanSell;

        KGOrder quotedBuy;
        KGOrder quotedFarBuy;
        KGOrder quotedSell;
        KGOrder quotedFarSell;

        KGOrder leanBuyMarket;
        KGOrder leanSellMarket;
        KGOrder quotedBuyMarket;
        KGOrder quotedSellMarket;
        KGOrder quotedFarBuyMarket;
        KGOrder quotedFarSellMarket;

        Detail.StopInfo stopBuyOrder1Info;
        Detail.StopInfo stopSellOrder1Info;
        Detail.StopInfo stopBuyOrder2Info;
        Detail.StopInfo stopSellOrder2Info;

        Dictionary<int, int> pendingOutrightHedgeOrders;

        Orders mOrders;

        List<KGOrder> hedgingOrders;

        Dictionary<int /*orderId*/, int /*volume*/> mPendingResubmissions;
        List<int> mIOCs;
        List<int> mIOCCancels;

        public Hedging(Strategy strategy)
        {
            mStrategy = strategy;

            leanBuy = new KGOrder();
            leanSell = new KGOrder();
            leanBuyMarket = new KGOrder();
            leanSellMarket = new KGOrder();

            quotedBuy = new KGOrder();
            quotedFarBuy = new KGOrder();
            quotedFarBuyMarket = new KGOrder();
            quotedBuyMarket = new KGOrder();

            quotedSell = new KGOrder();
            quotedFarSell = new KGOrder();
            quotedSellMarket = new KGOrder();
            quotedFarSellMarket = new KGOrder();

            mStrategy.strategyOrders.Add(leanBuy);
            mStrategy.strategyOrders.Add(leanSell);
            mStrategy.strategyOrders.Add(leanBuyMarket);
            mStrategy.strategyOrders.Add(leanSellMarket);

            mStrategy.strategyOrders.Add(quotedBuy);
            mStrategy.strategyOrders.Add(quotedFarBuy);
            mStrategy.strategyOrders.Add(quotedBuyMarket);
            mStrategy.strategyOrders.Add(quotedFarBuyMarket);

            mStrategy.strategyOrders.Add(quotedSell);
            mStrategy.strategyOrders.Add(quotedFarSell);
            mStrategy.strategyOrders.Add(quotedSellMarket);
            mStrategy.strategyOrders.Add(quotedFarSellMarket);

            hedgingOrders = new List<KGOrder>
            {
                leanBuy,
                leanSell,
                leanBuyMarket,
                leanSellMarket,

                quotedBuy,
                quotedFarBuy,
                quotedBuyMarket,
                quotedFarBuyMarket,

                quotedSell,
                quotedFarSell,
                quotedSellMarket,
                quotedFarSellMarket
            };

            stopBuyOrder1Info = new StopInfo();
            stopSellOrder1Info = new StopInfo();
            stopBuyOrder2Info = new StopInfo();
            stopSellOrder2Info = new StopInfo();

            pendingOutrightHedgeOrders = new Dictionary<int, int>();

            mPendingResubmissions = new Dictionary<int, int>();
            mIOCs = new List<int>();
            mIOCCancels = new List<int>();

            mOrders = mStrategy.orders;
        }

        private void Log(string message)
        {
            mStrategy.API.Log(String.Format("STG {0}: {1}", mStrategy.stgID, message));
            mStrategy.API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        private void CancelStrategy(string reason)
        {
            mStrategy.API.SetStrategyStatus(mStrategy.stgID, 0);
            mStrategy.API.CancelAllOrders(mStrategy.stgID);
            mStrategy.API.SendAlertBeep();
            mStrategy.API.Log(String.Format("CANCEL STG {0}: {1}", mStrategy.stgID, reason));
            mStrategy.API.SendToRemote(String.Format("CANCEL STG {0}: {1}", mStrategy.stgID, reason), KGConstants.EVENT_ERROR);
        }

        private int GetHedgeInstrument(int quantity)
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
                if (mStrategy.holding[mStrategy.leanIndex] + quantity > -P.maxOutrights)
                    if (mStrategy.bids[mStrategy.leanIndex].price + mStrategy.boxTargetPrice > bestBid)
                    {
                        bestBid = mStrategy.bids[mStrategy.leanIndex].price + mStrategy.boxTargetPrice;
                        hedgeIndex = mStrategy.leanIndex;
                    }
            }

            return hedgeIndex;
        }

        private void ManagePendingOrders(int orderId, int amount)
        {
            pendingOutrightHedgeOrders[orderId] = amount;
        }

        public void ManagePendingOrders(KGDeal deal)
        {
            if (pendingOutrightHedgeOrders.ContainsKey(deal.internalOrderNumber))
            {
                if (deal.isBuy)
                {
                    pendingOutrightHedgeOrders[deal.internalOrderNumber] -= deal.amount;
                }
                else
                {
                    pendingOutrightHedgeOrders[deal.internalOrderNumber] += deal.amount;
                }

                if (pendingOutrightHedgeOrders[deal.internalOrderNumber] == 0)
                {
                    pendingOutrightHedgeOrders.Remove(deal.internalOrderNumber);
                }
            }
        }

        private void PostStopOrder(int instrument, int leanInstrument, double limitPrice, double stopPrice, int volume, int stopVolume, Detail.Side side, int parentOrderId)
        {
            Log(String.Format("posting stop order for ins={0} lmt_price={1} stp_price={2} vol={3} stp_vol={4}", instrument, limitPrice, stopPrice, volume, stopVolume));
            if (side == Detail.Side.BUY)
            {
                if (stopBuyOrder1Info.inUse && stopBuyOrder2Info.inUse && stopBuyOrder1Info.instrument != instrument && stopBuyOrder2Info.instrument != instrument)
                {
                    CancelStrategy("max simultaneous stop orders reached");
                    return;
                }
                else if (stopBuyOrder1Info.inUse && stopBuyOrder2Info.inUse && stopBuyOrder1Info.instrument == instrument)
                {
                    stopBuyOrder1Info.volume += volume;
                    stopBuyOrder1Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopBuyOrder1 volume to {0}", stopBuyOrder1Info.volume));
                }
                else if (stopBuyOrder1Info.inUse && stopBuyOrder1Info.instrument == instrument)
                {
                    stopBuyOrder1Info.volume += volume;
                    stopBuyOrder1Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopBuyOrder1 volume to {0}", stopBuyOrder1Info.volume));
                }
                else if (stopBuyOrder1Info.inUse && stopBuyOrder2Info.inUse && stopBuyOrder2Info.instrument == instrument)
                {
                    stopBuyOrder2Info.volume += volume;
                    stopBuyOrder2Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopBuyOrder2 volume to {0}", stopBuyOrder2Info.volume));
                }
                else if (stopBuyOrder2Info.inUse && stopBuyOrder2Info.instrument == instrument)
                {
                    stopBuyOrder2Info.volume += volume;
                    stopBuyOrder2Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopBuyOrder2 volume to {0}", stopBuyOrder2Info.volume));
                }
                else if (stopBuyOrder1Info.inUse)
                {
                    Log("populating stopBuyOrder2");
                    stopBuyOrder2Info.inUse = true;
                    stopBuyOrder2Info.instrument = instrument;
                    stopBuyOrder2Info.stopPrice = stopPrice;
                    stopBuyOrder2Info.stopVolume = stopVolume;
                    stopBuyOrder2Info.leanInstrument = leanInstrument;
                    stopBuyOrder2Info.parent1OrderId = parentOrderId;
                    stopBuyOrder2Info.parent2OrderId = -1;
                    stopBuyOrder2Info.volume = volume;
                }
                else
                {
                    Log("populating stopBuyOrder1");
                    stopBuyOrder1Info.inUse = true;
                    stopBuyOrder1Info.instrument = instrument;
                    stopBuyOrder1Info.stopPrice = stopPrice;
                    stopBuyOrder1Info.stopVolume = stopVolume;
                    stopBuyOrder1Info.leanInstrument = leanInstrument;
                    stopBuyOrder1Info.parent1OrderId = parentOrderId;
                    stopBuyOrder1Info.parent2OrderId = -1;
                    stopBuyOrder1Info.volume = volume;
                }
            }
            else
            {
                if (stopSellOrder1Info.inUse && stopSellOrder2Info.inUse && stopSellOrder1Info.instrument != instrument && stopSellOrder2Info.instrument != instrument)
                {
                    CancelStrategy("max simultaneous stop orders reached");
                    return;
                }
                else if (stopSellOrder1Info.inUse && stopSellOrder2Info.inUse && stopSellOrder1Info.instrument == instrument)
                {
                    stopSellOrder1Info.volume += volume;
                    stopSellOrder1Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopSellOrder1 volume to {0}", stopSellOrder1Info.volume));
                }
                else if (stopSellOrder1Info.inUse && stopSellOrder1Info.instrument == instrument)
                {
                    stopSellOrder1Info.volume += volume;
                    stopSellOrder1Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopSellOrder1 volume to {0}", stopSellOrder1Info.volume));
                }
                else if (stopSellOrder1Info.inUse && stopSellOrder2Info.inUse && stopSellOrder2Info.instrument == instrument)
                {
                    stopSellOrder2Info.volume += volume;
                    stopSellOrder2Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopSellOrder2 volume to {0}", stopSellOrder2Info.volume));
                }
                else if (stopSellOrder2Info.inUse && stopSellOrder2Info.instrument == instrument)
                {
                    stopSellOrder2Info.volume += volume;
                    stopSellOrder2Info.parent2OrderId = parentOrderId;
                    Log(String.Format("incrementing stopSellOrder2 volume to {0}", stopSellOrder2Info.volume));
                }
                else if (stopSellOrder1Info.inUse)
                {
                    Log("populating stopSellOrder2");
                    stopSellOrder2Info.inUse = true;
                    stopSellOrder2Info.instrument = instrument;
                    stopSellOrder2Info.stopPrice = stopPrice;
                    stopSellOrder2Info.stopVolume = stopVolume;
                    stopSellOrder2Info.leanInstrument = leanInstrument;
                    stopSellOrder2Info.parent1OrderId = parentOrderId;
                    stopSellOrder2Info.parent2OrderId = -1;
                    stopSellOrder2Info.volume = volume;
                }
                else
                {
                    Log("populating stopSellOrder1");
                    stopSellOrder1Info.inUse = true;
                    stopSellOrder1Info.instrument = instrument;
                    stopSellOrder1Info.stopPrice = stopPrice;
                    stopSellOrder1Info.stopVolume = stopVolume;
                    stopSellOrder1Info.leanInstrument = leanInstrument;
                    stopSellOrder1Info.parent1OrderId = parentOrderId;
                    stopSellOrder1Info.parent2OrderId = -1;
                    stopSellOrder1Info.volume = volume;
                }
            }

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

                    if (stopInfo.parent1OrderId != -1)
                    {
                        pendingOutrightHedgeOrders.Remove(stopInfo.parent1OrderId);
                        mOrders.CancelOrder(stopInfo.parent1OrderId);
                    }

                    if (stopInfo.parent2OrderId != -1)
                    {
                        pendingOutrightHedgeOrders.Remove(stopInfo.parent2OrderId);
                        mOrders.CancelOrder(stopInfo.parent2OrderId);
                    }
                    break;
                case Detail.Hedge.LIMITPLUS:
                    stopInfo.inUse = false;

                    if (stopInfo.parent1OrderId != -1)
                    {
                        mOrders.CancelOrder(stopInfo.parent1OrderId);
                        pendingOutrightHedgeOrders.Remove(stopInfo.parent1OrderId);
                    }

                    if (stopInfo.parent2OrderId != -1)
                    {
                        mOrders.CancelOrder(stopInfo.parent2OrderId);
                        pendingOutrightHedgeOrders.Remove(stopInfo.parent2OrderId);
                    }

                    Hedge();
                    break;
                case Detail.Hedge.NONE:
                    break;
            }
        }

        public void EvaluateStops()
        {
            if (stopBuyOrder1Info.inUse)
            {
                OnStopTrigger(stopBuyOrder1Info, shouldStopBuy);
            }

            if (stopBuyOrder2Info.inUse)
            {
                OnStopTrigger(stopBuyOrder2Info, shouldStopBuy);
            }

            if (stopSellOrder1Info.inUse)
            {
                OnStopTrigger(stopSellOrder1Info, shouldStopSell);
            }

            if (stopSellOrder2Info.inUse)
            {
                OnStopTrigger(stopSellOrder2Info, shouldStopSell);
            }

            if (!stopBuyOrder1Info.inUse && !stopBuyOrder2Info.inUse && !stopSellOrder1Info.inUse && !stopSellOrder2Info.inUse)
            {
                Log("all stop orders executed");
                mStrategy.activeStopOrders = false;
            }
        }

        public void PropagateToStopOrder(int parentOrderId)
        {
            if (parentOrderId == stopBuyOrder1Info.parent1OrderId)
            {
                stopBuyOrder1Info.parent1OrderId = -1;

                if (stopBuyOrder1Info.parent1OrderId == -1 && stopBuyOrder1Info.parent2OrderId == -1)
                {
                    stopBuyOrder1Info.inUse = false;
                }
            }

            if (parentOrderId == stopBuyOrder2Info.parent1OrderId)
            {
                stopBuyOrder2Info.parent1OrderId = -1;

                if (stopBuyOrder2Info.parent1OrderId == -1 && stopBuyOrder2Info.parent2OrderId == -1)
                {
                    stopBuyOrder2Info.inUse = false;
                }
            }

            if (parentOrderId == stopSellOrder1Info.parent1OrderId)
            {
                stopSellOrder1Info.parent1OrderId = -1;

                if (stopSellOrder1Info.parent1OrderId == -1 && stopSellOrder1Info.parent2OrderId == -1)
                {
                    stopSellOrder1Info.inUse = false;
                }
            }

            if (parentOrderId == stopSellOrder2Info.parent1OrderId)
            {
                stopSellOrder2Info.parent1OrderId = -1;

                if (stopSellOrder2Info.parent1OrderId == -1 && stopSellOrder2Info.parent2OrderId == -1)
                {
                    stopSellOrder2Info.inUse = false;
                }
            }

            if (parentOrderId == stopBuyOrder1Info.parent2OrderId)
            {
                stopBuyOrder1Info.parent2OrderId = -1;

                if (stopBuyOrder1Info.parent1OrderId == -1 && stopBuyOrder1Info.parent2OrderId == -1)
                {
                    stopBuyOrder1Info.inUse = false;
                }
            }

            if (parentOrderId == stopBuyOrder2Info.parent2OrderId)
            {
                stopBuyOrder2Info.parent2OrderId = -1;

                if (stopBuyOrder2Info.parent1OrderId == -1 && stopBuyOrder2Info.parent2OrderId == -1)
                {
                    stopBuyOrder2Info.inUse = false;
                }
            }

            if (parentOrderId == stopSellOrder1Info.parent2OrderId)
            {
                stopSellOrder1Info.parent2OrderId = -1;

                if (stopSellOrder1Info.parent1OrderId == -1 && stopSellOrder1Info.parent2OrderId == -1)
                {
                    stopSellOrder1Info.inUse = false;
                }
            }

            if (parentOrderId == stopSellOrder2Info.parent2OrderId)
            {
                stopSellOrder2Info.parent2OrderId = -1;

                if (stopSellOrder2Info.parent1OrderId == -1 && stopSellOrder2Info.parent2OrderId == -1)
                {
                    stopSellOrder2Info.inUse = false;
                }
            }

            if (mStrategy.activeStopOrders && !stopBuyOrder1Info.inUse && !stopBuyOrder2Info.inUse && !stopSellOrder1Info.inUse && !stopSellOrder2Info.inUse)
            {
                mStrategy.activeStopOrders = false;
            }
        }

        public void OnOrder(KGOrder ord)
        {
            if (!mOrders.orderInTransientState(ord) && ord.bidSize < 0 && ord.askSize < 0)
            {
                if (mPendingResubmissions.ContainsKey(ord.internalOrderNumber))
                {
                    int size = mPendingResubmissions[ord.internalOrderNumber];
                    mPendingResubmissions.Remove(ord.internalOrderNumber);

                    Hedge();
                }

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
                    pendingOutrightHedgeOrders.Remove(ord.internalOrderNumber);
                    mOrders.CancelOnNextMD(ord);
                    Hedge();
                }
            }
        }

        private void CancelAllOrders()
        {
            foreach (var perOrder in pendingOutrightHedgeOrders)
            {
                mOrders.CancelOrder(perOrder.Key);
            }

            pendingOutrightHedgeOrders.Clear();
        }

        private int GetSumPendingOutrightHedgeOrders()
        {
            int sumPending = 0;

            foreach (var order in hedgingOrders)
            {
                if (mOrders.orderInUse(order) && order.orderStatus != 41)
                {
                    if (order.bidSize > 0 && order.askSize > 0)
                    {
                        throw new Exception("ERROR: order has both bid and ask size");
                    }
                    if (order.bidSize > 0)
                    {
                        sumPending += order.bidSize;
                    }
                    if (order.askSize > 0)
                    {
                        sumPending -= order.askSize;
                    }
                }
            }
            return sumPending;
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        private void LimitPlusBuyLean(int n)
        {
            var order = leanBuy;

            double price = mStrategy.asks[mStrategy.leanIndex].price - mStrategy.tickSize;

            if (!pricesAreEqual(order.ask, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, mStrategy.leanIndex, Side.BUY, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    return;
                }

                ManagePendingOrders(order.internalOrderNumber, n);
            }

            PostStopOrder(mStrategy.leanIndex, mStrategy.leanIndex, price, mStrategy.asks[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.BUY, order.internalOrderNumber);
        }

        private void LimitPlusSellLean(int n)
        {
            var order = leanSell;

            double price = mStrategy.bids[mStrategy.leanIndex].price + mStrategy.tickSize;

            if (!pricesAreEqual(order.ask, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, mStrategy.leanIndex, Side.SELL, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    return;
                }

                ManagePendingOrders(order.internalOrderNumber, -n);

            }

            PostStopOrder(mStrategy.leanIndex, mStrategy.leanIndex, price, mStrategy.bids[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.SELL, order.internalOrderNumber);
        }

        private void LimitPlusBuyQuoted(int n)
        {
            var order = quotedBuy;

            double price = mStrategy.asks[mStrategy.quoteIndex].price - mStrategy.tickSize;

            if (!pricesAreEqual(order.bid, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, mStrategy.quoteIndex, Side.BUY, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    pendingOutrightHedgeOrders.Remove(order.internalOrderNumber);
                    return;
                }

                ManagePendingOrders(order.internalOrderNumber, n);
            }

            PostStopOrder(mStrategy.quoteIndex, mStrategy.leanIndex, price, mStrategy.asks[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.BUY, order.internalOrderNumber);
        }

        private void LimitPlusBuyQuotedFar(int n)
        {
            var order = quotedFarBuy;

            double price = mStrategy.asks[mStrategy.farIndex].price - mStrategy.tickSize;

            if (!pricesAreEqual(order.bid, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, mStrategy.farIndex, Side.BUY, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    return;
                }

                ManagePendingOrders(order.internalOrderNumber, n);
            }

            PostStopOrder(mStrategy.farIndex, mStrategy.leanIndex, price, mStrategy.asks[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.BUY, order.internalOrderNumber);
        }

        private void LimitPlusSellQuoted(int n)
        {
            var order = quotedSell;

            double price = mStrategy.bids[mStrategy.quoteIndex].price + mStrategy.tickSize;

            if (!pricesAreEqual(order.ask, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, mStrategy.quoteIndex, Side.SELL, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    return;
                }

                ManagePendingOrders(order.internalOrderNumber, -n);
            }

            PostStopOrder(mStrategy.quoteIndex, mStrategy.leanIndex, price, mStrategy.bids[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.SELL, order.internalOrderNumber);
        }

        private void LimitPlusSellQuotedFar(int n)
        {
            var order = quotedFarSell;

            double price = mStrategy.bids[mStrategy.farIndex].price + mStrategy.tickSize;

            if (!pricesAreEqual(order.ask, price) || !mOrders.orderInUse(order) || order.orderStatus == 41)
            {
                int orderId = mOrders.SendOrder(order, mStrategy.farIndex, Side.SELL, price, n, "HEDGE");

                if (orderId == -1)
                {
                    mOrders.CancelOrder(order.internalOrderNumber);
                    return;
                }

                ManagePendingOrders(order.internalOrderNumber, -n);
            }

            PostStopOrder(mStrategy.farIndex, mStrategy.leanIndex, price, mStrategy.bids[mStrategy.leanIndex].price, n, mStrategy.limitPlusSize, Side.SELL, order.internalOrderNumber);
        }

        private int BuyLean(int n)
        {
            int orderId = mOrders.SendOrder(leanBuyMarket, mStrategy.leanIndex, Side.BUY, mStrategy.asks[mStrategy.leanIndex].price, n, "HEDGE");

            ManagePendingOrders(orderId, n);
            return orderId;
        }

        private int SellLean(int n)
        {
            int orderId = mOrders.SendOrder(leanSellMarket, mStrategy.leanIndex, Side.SELL, mStrategy.bids[mStrategy.leanIndex].price, n, "HEDGE");

            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int SellQuoted(int n)
        {
            int orderId = mOrders.SendOrder(quotedSellMarket, mStrategy.quoteIndex, Side.SELL, mStrategy.bids[mStrategy.quoteIndex].price, n, "HEDGE");

            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int BuyQuoted(int n)
        {
            int orderId = mOrders.SendOrder(quotedBuyMarket, mStrategy.quoteIndex, Side.BUY, mStrategy.asks[mStrategy.quoteIndex].price, n, "HEDGE");

            ManagePendingOrders(orderId, n);
            return orderId;
        }

        private int SellQuotedFar(int n)
        {
            int orderId = mOrders.SendOrder(quotedFarSellMarket, mStrategy.farIndex, Side.SELL, mStrategy.bids[mStrategy.farIndex].price, n, "HEDGE");

            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int BuyQuotedFar(int n)
        {
            int orderId = mOrders.SendOrder(quotedFarBuyMarket, mStrategy.farIndex, Side.BUY, mStrategy.asks[mStrategy.farIndex].price, n, "HEDGE");

            ManagePendingOrders(orderId, n);
            return orderId;
        }

        public void Hedge()
        {
            int netPosition = mStrategy.GetNetPosition();

            if (netPosition == 0)
            {
                CancelAllOrders();
                return;
            }

            int pending = GetSumPendingOutrightHedgeOrders();

            int quantity = netPosition + pending;

            if (quantity == 0)
                return;

            Log(String.Format("hedging holding={0}, qty={1}", netPosition, quantity));

            int hedgeInstrument = GetHedgeInstrument(quantity);

            if (hedgeInstrument == mStrategy.quoteIndex)
            {
                if (quantity > 0)
                {
                    LimitPlusSellQuoted(quantity);
                }
                else if (quantity < 0)
                {
                    LimitPlusBuyQuoted(-quantity);
                }
                return;
            }

            if (hedgeInstrument == mStrategy.farIndex)
            {
                if (quantity > 0)
                {
                    LimitPlusSellQuotedFar(quantity);
                }
                else if (quantity < 0)
                {
                    LimitPlusBuyQuotedFar(-quantity);
                }
                return;
            }

            if (hedgeInstrument == mStrategy.leanIndex)
            {
                if (quantity > 0)
                {
                    LimitPlusSellLean(quantity);
                }
                else if (quantity < 0)
                {
                    LimitPlusBuyLean(-quantity);
                }
                return;
            }
        }

        public int HedgeNow(int quantity)
        {
            if (quantity == 0)
            {
                return -1;
            }

            int hedgeInstrument = GetHedgeInstrument(quantity);

            if (hedgeInstrument == mStrategy.quoteIndex)
            {
                if (quantity < 0)
                {
                    int orderId = SellQuoted(-quantity);
                    mIOCs.Add(orderId);
                    return orderId;
                }
                else if (quantity > 0)
                {
                    int orderId = BuyQuoted(quantity);
                    mIOCs.Add(orderId);
                    return orderId;
                }
            }

            if (hedgeInstrument == mStrategy.farIndex)
            {
                if (quantity < 0)
                {
                    int orderId = SellQuotedFar(-quantity);
                    mIOCs.Add(orderId);
                    return orderId;
                }
                else if (quantity > 0)
                {
                    int orderId = BuyQuotedFar(quantity);
                    mIOCs.Add(orderId);
                    return orderId;
                }
            }

            if (hedgeInstrument == mStrategy.leanIndex)
            {
                if (quantity < 0)
                {
                    int orderId = SellLean(-quantity);
                    mIOCs.Add(orderId);
                    return orderId;
                }
                else if (quantity > 0)
                {
                    int orderId = BuyLean(quantity);
                    mIOCs.Add(orderId);
                    return orderId;
                }
            }

            return -1;
        }
    }
}