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

        public int parent1OrderId;
        public int parent2OrderId;

        public int volume;
    }

    public enum HedgeKind
    {
        OUTRIGHT = 0,
        BASE_SPREAD = 1,
        BV = 2
    }
}


namespace StrategyRunner
{

    public class Hedging
    {
        Strategy mStrategy;

        KGOrder leanBuy;
        KGOrder leanBuyFromFar;
        KGOrder leanSell;
        KGOrder leanSellFromFar;

        KGOrder quotedBuy;
        KGOrder quotedBuyFromFar;
        KGOrder quotedFarBuy;
        KGOrder quotedFarBuyFromFar;
        KGOrder quotedSell;
        KGOrder quotedSellFromFar;
        KGOrder quotedFarSell;
        KGOrder quotedFarSellFromFar;

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

        List<int> mHedgeIndices;

        public int pendingOutrightHedges;
        Dictionary<int, int> pendingOutrightHedgeOrders;

        Orders mOrders;

        Dictionary<int /*cancelledOrderId*/, int /*volume*/> mPendingResubmissions;

        public Hedging(Strategy strategy)
        {
            mStrategy = strategy;
            mHedgeIndices = new List<int>();

            mHedgeIndices.Add(mStrategy.quoteIndex);
            mHedgeIndices.Add(mStrategy.quoteFarIndex);
            mHedgeIndices.Add(mStrategy.leanIndex);

            leanBuy = new KGOrder();
            leanBuyFromFar = new KGOrder();
            leanSell = new KGOrder();
            leanSellFromFar = new KGOrder();
            leanBuyMarket = new KGOrder();
            leanSellMarket = new KGOrder();

            quotedBuy = new KGOrder();
            quotedBuyFromFar = new KGOrder();
            quotedFarBuy = new KGOrder();
            quotedFarBuyFromFar = new KGOrder();
            quotedFarBuyMarket = new KGOrder();
            quotedBuyMarket = new KGOrder();

            quotedSell = new KGOrder();
            quotedSellFromFar = new KGOrder();
            quotedFarSell = new KGOrder();
            quotedFarSellFromFar = new KGOrder();
            quotedSellMarket = new KGOrder();
            quotedFarSellMarket = new KGOrder();

            mStrategy.strategyOrders.Add(leanBuy);
            mStrategy.strategyOrders.Add(leanBuyFromFar);
            mStrategy.strategyOrders.Add(leanSell);
            mStrategy.strategyOrders.Add(leanSellFromFar);
            mStrategy.strategyOrders.Add(leanBuyMarket);
            mStrategy.strategyOrders.Add(leanSellMarket);

            mStrategy.strategyOrders.Add(quotedBuy);
            mStrategy.strategyOrders.Add(quotedFarBuy);
            mStrategy.strategyOrders.Add(quotedBuyFromFar);
            mStrategy.strategyOrders.Add(quotedFarBuyFromFar);
            mStrategy.strategyOrders.Add(quotedBuyMarket);
            mStrategy.strategyOrders.Add(quotedFarBuyMarket);

            mStrategy.strategyOrders.Add(quotedSell);
            mStrategy.strategyOrders.Add(quotedSellFromFar);
            mStrategy.strategyOrders.Add(quotedFarSell);
            mStrategy.strategyOrders.Add(quotedFarSellFromFar);
            mStrategy.strategyOrders.Add(quotedSellMarket);
            mStrategy.strategyOrders.Add(quotedFarSellMarket);

            stopBuyOrder1Info = new StopInfo();
            stopSellOrder1Info = new StopInfo();
            stopBuyOrder2Info = new StopInfo();
            stopSellOrder2Info = new StopInfo();

            pendingOutrightHedgeOrders = new Dictionary<int, int>();

            mPendingResubmissions = new Dictionary<int, int>();

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

        private double GetOffset()
        {
            switch (mStrategy.stgID)
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

        private int GetLimitplusSize(int volume)
        {
            switch (mStrategy.stgID) //this assumes stgID is per expiry, and returns the usual euribor limitplus sizes
            {
                case 1:
                    return 2000 + volume * 3;
                case 2:
                    return 1000 + volume * 3;
                case 3:
                    return 500 + volume * 3;
                case 7:
                    return 1000 + volume * 3;
                case 8:
                    return 500 + volume * 3;
                default:
                    return 200 + volume * 3;
            }
        }

        private int GetHedgeInstrument(int quantity)
        {
            int hedgeIndex = -1;
            double bestOffer = double.MaxValue;
            double bestBid = double.MinValue;

            foreach (var index in mHedgeIndices)
            {
                if (Math.Abs(mStrategy.holding[index] + quantity) > P.maxOutrights)
                    continue;

                if (quantity > 0)
                {
                    if (mStrategy.asks[index].price < bestOffer)
                    {
                        bestOffer = mStrategy.asks[index].price;
                        hedgeIndex = index;
                    }
                    else if (index == mStrategy.leanIndex && mStrategy.asks[index].price + GetOffset() < bestOffer)
                    {
                        bestOffer = mStrategy.asks[index].price + GetOffset();
                        hedgeIndex = index;
                    }
                    else if (mStrategy.asks[index].price == bestOffer)
                    {
                        if (hedgeIndex == mStrategy.leanIndex)
                        {
                            hedgeIndex = index;
                        }
                        else if (hedgeIndex == mStrategy.quoteFarIndex && index == mStrategy.quoteIndex)
                        {
                            hedgeIndex = index;
                        }
                    }
                }

                if (quantity < 0)
                {
                    if (mStrategy.bids[index].price > bestBid)
                    {
                        bestBid = mStrategy.bids[index].price;
                        hedgeIndex = index;
                    }
                    else if (index == mStrategy.leanIndex && mStrategy.bids[index].price + GetOffset() > bestBid)
                    {
                        bestBid = mStrategy.bids[index].price + GetOffset();
                        hedgeIndex = index;
                    }
                    else if (mStrategy.bids[index].price == bestBid)
                    {
                        if (hedgeIndex == mStrategy.leanIndex)
                        {
                            hedgeIndex = index;
                        }
                        else if (hedgeIndex == mStrategy.quoteFarIndex && index == mStrategy.quoteIndex)
                        {
                            hedgeIndex = index;
                        }
                    }
                }
            }

            return hedgeIndex;
        }

        private void ManagePendingOrders(int orderId, int amount)
        {
            pendingOutrightHedgeOrders[orderId] = amount;
            pendingOutrightHedges += amount;
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

                pendingOutrightHedges = 0;
                foreach (var perOrder in pendingOutrightHedgeOrders)
                {
                    pendingOutrightHedges += perOrder.Value;
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
                    Log("incrementing stopBuyOrder1 volume");
                    stopBuyOrder1Info.volume += volume;
                    stopBuyOrder1Info.parent2OrderId = parentOrderId;
                }
                else if (stopBuyOrder1Info.inUse && stopBuyOrder1Info.instrument == instrument)
                {
                    Log("incrementing stopBuyOrder1 volume");
                    stopBuyOrder1Info.volume += volume;
                    stopBuyOrder1Info.parent2OrderId = parentOrderId;
                }
                else if (stopBuyOrder1Info.inUse && stopBuyOrder2Info.inUse && stopBuyOrder2Info.instrument == instrument)
                {
                    Log("incrementing stopBuyOrder2 volume");
                    stopBuyOrder2Info.volume += volume;
                    stopBuyOrder2Info.parent2OrderId = parentOrderId;
                }
                else if (stopBuyOrder2Info.inUse && stopBuyOrder2Info.instrument == instrument)
                {
                    Log("incrementing stopBuyOrder2 volume");
                    stopBuyOrder2Info.volume += volume;
                    stopBuyOrder2Info.parent2OrderId = parentOrderId;
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
                    Log("incrementing stopSellOrder1 volume");
                    stopSellOrder1Info.volume += volume;
                    stopSellOrder1Info.parent2OrderId = parentOrderId;
                }
                else if (stopSellOrder1Info.inUse && stopSellOrder1Info.instrument == instrument)
                {
                    Log("incrementing stopSellOrder1 volume");
                    stopSellOrder1Info.volume += volume;
                    stopSellOrder1Info.parent2OrderId = parentOrderId;
                }
                else if (stopSellOrder1Info.inUse && stopSellOrder2Info.inUse && stopSellOrder2Info.instrument == instrument)
                {
                    Log("incrementing stopSellOrder2 volume");
                    stopSellOrder2Info.volume += volume;
                    stopSellOrder2Info.parent2OrderId = parentOrderId;
                }
                else if (stopSellOrder2Info.inUse && stopSellOrder2Info.instrument == instrument)
                {
                    Log("incrementing stopSellOrder2 volume");
                    stopSellOrder2Info.volume += volume;
                    stopSellOrder2Info.parent2OrderId = parentOrderId;
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

        private bool shouldStopBuy(StopInfo info)
        {
            int index = info.leanInstrument != -1 ? info.leanInstrument : info.instrument;
            bool volumeAtStopPriceLow = (mStrategy.asks[index].price == info.stopPrice && mStrategy.asks[index].qty < info.stopVolume);
            bool marketRanAway = mStrategy.asks[index].price > info.stopPrice;
            bool theoRanAway = mStrategy.theos[index] > info.stopPrice + 0.001;
            return
                (volumeAtStopPriceLow || marketRanAway)
                && theoRanAway;
        }

        private bool shouldStopSell(StopInfo info)
        {
            int index = info.leanInstrument != -1 ? info.leanInstrument : info.instrument;
            bool volumeAtStopPriceLow = (mStrategy.bids[index].price == info.stopPrice && mStrategy.bids[index].qty < info.stopVolume);
            bool marketRanAway = mStrategy.bids[index].price < info.stopPrice;
            bool theoRanAway = mStrategy.theos[index] < info.stopPrice + 0.001;
            return
                (volumeAtStopPriceLow || marketRanAway)
                && theoRanAway;
        }

        private void ReassignPending(int parentOrderId, int stopOrderId)
        {
            if (pendingOutrightHedgeOrders.ContainsKey(parentOrderId))
            {
                pendingOutrightHedgeOrders.Remove(parentOrderId);
            }
        }

        public void EvaluateStops()
        {
            if (stopBuyOrder1Info.inUse)
            {
                if (shouldStopBuy(stopBuyOrder1Info))
                {
                    Log("executing stopBuyOrder1");
                    int orderId = HedgeNow(stopBuyOrder1Info.volume);
                    stopBuyOrder1Info.inUse = false;

                    if (stopBuyOrder1Info.parent1OrderId != -1)
                    {
                        ReassignPending(stopBuyOrder1Info.parent1OrderId, orderId);
                        mOrders.CancelOrder(stopBuyOrder1Info.parent1OrderId);
                    }


                    if (stopBuyOrder1Info.parent2OrderId != -1)
                    {
                        ReassignPending(stopBuyOrder1Info.parent2OrderId, orderId);
                        mOrders.CancelOrder(stopBuyOrder1Info.parent2OrderId);
                    }
                }
            }

            if (stopBuyOrder2Info.inUse)
            {
                if (shouldStopBuy(stopBuyOrder2Info))
                {
                    Log("executing stopBuyOrder2");
                    int orderId = HedgeNow(stopBuyOrder2Info.volume);
                    stopBuyOrder2Info.inUse = false;

                    if (stopBuyOrder2Info.parent1OrderId != -1)
                    {
                        ReassignPending(stopBuyOrder2Info.parent1OrderId, orderId);
                        mOrders.CancelOrder(stopBuyOrder2Info.parent1OrderId);
                    }

                    if (stopBuyOrder2Info.parent2OrderId != -1)
                    {
                        ReassignPending(stopBuyOrder2Info.parent2OrderId, orderId);
                        mOrders.CancelOrder(stopBuyOrder2Info.parent2OrderId);
                    }
                }
            }

            if (stopSellOrder1Info.inUse)
            {
                if (shouldStopSell(stopSellOrder1Info))
                {
                    Log("executing stopSellOrder1");
                    int orderId = HedgeNow(-stopSellOrder1Info.volume);
                    stopSellOrder1Info.inUse = false;

                    if (stopSellOrder1Info.parent1OrderId != -1)
                    {
                        ReassignPending(stopSellOrder1Info.parent1OrderId, orderId);
                        mOrders.CancelOrder(stopSellOrder1Info.parent1OrderId);
                    }

                    if (stopSellOrder1Info.parent2OrderId != -1)
                    {
                        ReassignPending(stopSellOrder1Info.parent2OrderId, orderId);
                        mOrders.CancelOrder(stopSellOrder1Info.parent2OrderId);
                    }
                }
            }

            if (stopSellOrder2Info.inUse)
            {
                if (shouldStopSell(stopSellOrder2Info))
                {
                    Log("executing stopSellOrder2");
                    int orderId = HedgeNow(-stopBuyOrder2Info.volume);
                    stopSellOrder2Info.inUse = false;

                    if (stopSellOrder2Info.parent1OrderId != -1)
                    {
                        ReassignPending(stopSellOrder2Info.parent1OrderId, orderId);
                        mOrders.CancelOrder(stopSellOrder2Info.parent1OrderId);
                    }

                    if (stopSellOrder2Info.parent2OrderId != -1)
                    {
                        ReassignPending(stopSellOrder2Info.parent2OrderId, orderId);
                        mOrders.CancelOrder(stopSellOrder2Info.parent2OrderId);
                    }
                }
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

        public void OnOrder(KGOrder ord, int instrumentIndex)
        {
            if (mPendingResubmissions.ContainsKey(ord.internalOrderNumber))
            {
                int size = mPendingResubmissions[ord.internalOrderNumber];
                mPendingResubmissions.Remove(ord.internalOrderNumber);

                Hedge(size, Source.NEAR);
            }
        }

        private void LimitPlusBuyLean(int n, Detail.HedgeKind kind, Detail.Source source)
        {
            var order = leanBuy;

            switch (source)
            {
                case Source.NEAR:
                    break;
                case Source.FAR:
                    order = leanBuyFromFar;
                    break;
            }

            int orderId = mOrders.SendOrder(order, mStrategy.leanIndex, Side.BUY, mStrategy.bids[mStrategy.leanIndex].price, n, "HEDGE");
            
            if (orderId == -1)
            {
                mPendingResubmissions[order.internalOrderNumber] = n;
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }

            ManagePendingOrders(order.internalOrderNumber, n);
            PostStopOrder(mStrategy.leanIndex, mStrategy.leanIndex, mStrategy.bids[mStrategy.leanIndex].price, mStrategy.asks[mStrategy.leanIndex].price, n, GetLimitplusSize(n) + 200, Side.BUY, order.internalOrderNumber);
        }

        private void LimitPlusSellLean(int n, Detail.HedgeKind kind, Detail.Source source)
        {
            var order = leanSell;

            switch (source)
            {
                case Source.NEAR:
                    break;
                case Source.FAR:
                    order = leanSellFromFar;
                    break;
            }

            int orderId = mOrders.SendOrder(order, mStrategy.leanIndex, Side.SELL, mStrategy.asks[mStrategy.leanIndex].price, n, "HEDGE");

            if (orderId == -1)
            {
                mPendingResubmissions[order.internalOrderNumber] = n;
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }

            ManagePendingOrders(order.internalOrderNumber, -n);
            PostStopOrder(mStrategy.leanIndex, mStrategy.leanIndex, mStrategy.asks[mStrategy.leanIndex].price, mStrategy.bids[mStrategy.leanIndex].price, n, GetLimitplusSize(n) + 200, Side.SELL, order.internalOrderNumber);
        }

        private void LimitPlusBuyQuoted(int n, Detail.HedgeKind kind, Detail.Source source)
        {
            var order = quotedBuy;

            switch (source)
            {
                case Source.NEAR:
                    break;
                case Source.FAR:
                    order = quotedBuyFromFar;
                    break;
            }

            int orderId = mOrders.SendOrder(order, mStrategy.quoteIndex, Side.BUY, mStrategy.bids[mStrategy.quoteIndex].price, n, "HEDGE");

            if (orderId == -1)
            {
                mPendingResubmissions[order.internalOrderNumber] = n;
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }

            ManagePendingOrders(order.internalOrderNumber, n);
            PostStopOrder(mStrategy.quoteIndex, mStrategy.leanIndex, mStrategy.bids[mStrategy.quoteIndex].price, mStrategy.asks[mStrategy.leanIndex].price, n, GetLimitplusSize(n) + 200, Side.BUY, order.internalOrderNumber);
        }

        private void LimitPlusBuyQuotedFar(int n, Detail.HedgeKind kind, Detail.Source source)
        {
            var order = quotedFarBuy;

            switch (source)
            {
                case Source.NEAR:
                    break;
                case Source.FAR:
                    order = quotedFarBuyFromFar;
                    break;
            }

            int orderId = mOrders.SendOrder(order, mStrategy.quoteFarIndex, Side.BUY, mStrategy.bids[mStrategy.quoteFarIndex].price, n, "HEDGE");

            if (orderId == -1)
            {
                mPendingResubmissions[order.internalOrderNumber] = n;
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }

            ManagePendingOrders(order.internalOrderNumber, n);
            PostStopOrder(mStrategy.quoteFarIndex, mStrategy.leanIndex, mStrategy.bids[mStrategy.quoteFarIndex].price, mStrategy.asks[mStrategy.leanIndex].price, n, GetLimitplusSize(n) + 200, Side.BUY, order.internalOrderNumber);
        }

        private void LimitPlusSellQuoted(int n, Detail.HedgeKind kind, Detail.Source source)
        {
            var order = quotedSell;

            switch (source)
            {
                case Source.NEAR:
                    break;
                case Source.FAR:
                    order = quotedSellFromFar;
                    break;
            }

            int orderId = mOrders.SendOrder(order, mStrategy.quoteIndex, Side.SELL, mStrategy.asks[mStrategy.quoteIndex].price, n, "HEDGE");

            if (orderId == -1)
            {
                mPendingResubmissions[order.internalOrderNumber] = n;
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }

            ManagePendingOrders(order.internalOrderNumber, -n);
            PostStopOrder(mStrategy.quoteIndex, mStrategy.leanIndex, mStrategy.asks[mStrategy.quoteIndex].price, mStrategy.bids[mStrategy.leanIndex].price, n, GetLimitplusSize(n) + 200, Side.SELL, order.internalOrderNumber);
        }

        private void LimitPlusSellQuotedFar(int n, Detail.HedgeKind kind, Detail.Source source)
        {
            var order = quotedFarSell;

            if (mOrders.orderInUse(order))
            {
                mPendingResubmissions[order.internalOrderNumber] = n;
                mOrders.CancelOrder(order.internalOrderNumber);
                return;
            }

            switch (source)
            {
                case Source.NEAR:
                    break;
                case Source.FAR:
                    order = quotedFarSellFromFar;
                    break;
            }

            int orderId = mOrders.SendOrder(order, mStrategy.quoteFarIndex, Side.SELL, mStrategy.asks[mStrategy.quoteFarIndex].price, n, "HEDGE");
            ManagePendingOrders(order.internalOrderNumber, -n);
            PostStopOrder(mStrategy.quoteFarIndex, mStrategy.leanIndex, mStrategy.asks[mStrategy.quoteFarIndex].price, mStrategy.bids[mStrategy.leanIndex].price, n, GetLimitplusSize(n) + 200, Side.SELL, order.internalOrderNumber);
        }

        private int BuyLean(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(leanBuyMarket, mStrategy.leanIndex, Side.BUY, mStrategy.asks[mStrategy.leanIndex].price, n, "HEDGE");
            ManagePendingOrders(orderId, n);
            return orderId;
        }

        private int SellLean(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(leanSellMarket, mStrategy.leanIndex, Side.SELL, mStrategy.bids[mStrategy.leanIndex].price, n, "HEDGE");
            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int SellQuoted(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(quotedSellMarket, mStrategy.quoteIndex, Side.SELL, mStrategy.bids[mStrategy.quoteIndex].price, n, "HEDGE");
            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int BuyQuoted(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(quotedBuyMarket, mStrategy.quoteIndex, Side.BUY, mStrategy.asks[mStrategy.quoteIndex].price, n, "HEDGE");
            ManagePendingOrders(orderId, n);
            return orderId;
        }

        private int SellQuotedFar(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(quotedFarSellMarket, mStrategy.quoteFarIndex, Side.SELL, mStrategy.bids[mStrategy.quoteFarIndex].price, n, "HEDGE");
            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int BuyQuotedFar(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(quotedFarBuyMarket, mStrategy.quoteFarIndex, Side.BUY, mStrategy.asks[mStrategy.quoteFarIndex].price, n, "HEDGE");
            ManagePendingOrders(orderId, n);
            return orderId;
        }

        public void Hedge(int quantity, Source source)
        {
            if (quantity == 0)
            {
                return;
            }

            int hedgeInstrument = GetHedgeInstrument(quantity);

            if (hedgeInstrument == mStrategy.quoteIndex)
            {
                if (quantity < 0)
                {
                    LimitPlusSellQuoted(-quantity, HedgeKind.OUTRIGHT, source);
                }
                else if (quantity > 0)
                {
                    LimitPlusBuyQuoted(quantity, HedgeKind.OUTRIGHT, source);
                }
                return;
            }

            if (hedgeInstrument == mStrategy.quoteFarIndex)
            {
                if (quantity < 0)
                {
                    LimitPlusSellQuotedFar(-quantity, HedgeKind.OUTRIGHT, source);
                }
                else if (quantity > 0)
                {
                    LimitPlusBuyQuotedFar(quantity, HedgeKind.OUTRIGHT, source);
                }
                return;
            }

            if (hedgeInstrument == mStrategy.leanIndex)
            {
                if (quantity < 0)
                {
                    LimitPlusSellLean(-quantity, HedgeKind.OUTRIGHT, source);
                }
                else if (quantity > 0)
                {
                    LimitPlusBuyLean(quantity, HedgeKind.OUTRIGHT, source);
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
                    int orderId = SellQuoted(-quantity, HedgeKind.OUTRIGHT);
                    return orderId;
                }
                else if (quantity > 0)
                {
                    int orderId = BuyQuoted(quantity, HedgeKind.OUTRIGHT);
                    return orderId;
                }
            }

            if (hedgeInstrument == mStrategy.quoteFarIndex)
            {
                if (quantity < 0)
                {
                    int orderId = SellQuotedFar(-quantity, HedgeKind.OUTRIGHT);
                    return orderId;
                }
                else if (quantity > 0)
                {
                    int orderId = BuyQuotedFar(quantity, HedgeKind.OUTRIGHT);
                    return orderId;
                }
            }

            if (hedgeInstrument == mStrategy.leanIndex)
            {
                if (quantity < 0)
                {
                    int orderId = SellLean(-quantity, HedgeKind.OUTRIGHT);
                    return orderId;
                }
                else if (quantity > 0)
                {
                    int orderId = BuyLean(quantity, HedgeKind.OUTRIGHT);
                    return orderId;
                }
            }

            return -1;
        }
    }

}