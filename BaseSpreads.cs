using Detail;
using System;
using System.IO;
using System.Text.Json;
using KGClasses;
using System.Collections.Generic;

namespace Detail
{
    public class PositionInfo
    {
        public double averagePrice { get; set; }
        public double averageLeanPrice { get; set; }
        public double averageQuotePrice { get; set; }
    }
}

//TODO: replace current logic with GetDirection

namespace StrategyRunner
{
	public class BaseSpreads
	{
        Quoter mQuoter;
        
        Orders mOrders;

        KGOrder mIcsBuy;
        KGOrder mIcsSell;

        KGOrder mLeanBuy;
        KGOrder mLeanSell;
        KGOrder mQuotedBuy;
        KGOrder mQuotedSell;

        PositionInfo positionInfo;
        string positionInfoFile;
        JsonSerializerOptions jsonOptions;

        double baseSpreadBid;
        double baseSpreadAsk;

        int unhedgedOutrightPosition;
        int baseSpreadPosition;

        int pendingBaseSpreadOrders;
        Dictionary<int, int> mPendingBaseSpreadOrderAmounts;

        Throttler.Throttler mBaseSpreadThrottler;

        public BaseSpreads(Quoter quoter)
		{
            mQuoter = quoter;
            mOrders = mQuoter.orders;

            mIcsBuy = new KGOrder();
            mIcsSell = new KGOrder();

            mLeanBuy = new KGOrder();
            mLeanSell = new KGOrder();
            mQuotedBuy = new KGOrder();
            mQuotedSell = new KGOrder();

            mQuoter.strategyOrders.Add(mIcsBuy);
            mQuoter.strategyOrders.Add(mIcsSell);
            mQuoter.strategyOrders.Add(mLeanBuy);
            mQuoter.strategyOrders.Add(mLeanSell);
            mQuoter.strategyOrders.Add(mQuotedBuy);
            mQuoter.strategyOrders.Add(mQuotedSell);

            mPendingBaseSpreadOrderAmounts = new Dictionary<int, int>();

            TimeSpan tBs = new TimeSpan(0, 0, 0, 0, P.baseSpreadThrottleSeconds * 1000);
            mBaseSpreadThrottler = new Throttler.Throttler(P.baseSpreadThrottleVolume, tBs);
            positionInfoFile = "position/" + mQuoter.config.quoteInstrument + "_position_info.json";
            jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            if (File.Exists(positionInfoFile))
            {
                string jsonFromFile = File.ReadAllText(positionInfoFile);
                positionInfo = JsonSerializer.Deserialize<Detail.PositionInfo>(jsonFromFile);
            }
            else
            {
                positionInfo = new PositionInfo();
                Log("WRN: position file not found");
            }
        }

        public void OnUpdatedParams()
        {
            mBaseSpreadThrottler.updateMaxVolume(P.baseSpreadThrottleVolume);
            mBaseSpreadThrottler.updateTimespan(P.baseSpreadThrottleSeconds);
        }

        private void Log(string message)
        {
            mQuoter.API.Log(String.Format("STG {0}: {1}", mQuoter.stgID, message));
            mQuoter.API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        private double GetDesired()
        {
            switch (mQuoter.stgID) //this assumes stgID is per expiry, might wanna add some config instead
            {
                case 1:
                    return P.desiredEus1;
                case 2:
                    return P.desiredEus2;
                case 3:
                    return P.desiredEus3;
                case 4:
                    return P.desiredEus4;
                case 5:
                    return P.desiredEus5;
                case 6:
                    return P.desiredEus6;
                default:
                    throw new Exception("offset index out of bounds");
            }
        }

        public void UpdateBaseSpreadsAveragePrice(int tradeAmount, double tradePrice, int index)
        {
            if (index == mQuoter.leanIndex)
            {
                double weightedCurrentAveragePrice = positionInfo.averageLeanPrice * Math.Abs(mQuoter.holding[mQuoter.leanIndex] - tradeAmount);
                double newComponent = Math.Abs(tradeAmount) * tradePrice;
                positionInfo.averageLeanPrice = mQuoter.holding[mQuoter.leanIndex] != 0 ? (weightedCurrentAveragePrice + newComponent) / Math.Abs(mQuoter.holding[mQuoter.leanIndex]) : 0;
            }

            if (index == mQuoter.quoteIndex || index == mQuoter.quoteFarIndex)
            {
                double weightedCurrentAveragePrice = positionInfo.averageQuotePrice * Math.Abs(mQuoter.holding[mQuoter.quoteIndex] + mQuoter.holding[mQuoter.quoteFarIndex] - tradeAmount);
                double newComponent = Math.Abs(tradeAmount) * tradePrice;
                positionInfo.averageQuotePrice = mQuoter.holding[mQuoter.quoteIndex] + mQuoter.holding[mQuoter.quoteFarIndex] != 0 ? (weightedCurrentAveragePrice + newComponent) / Math.Abs(mQuoter.holding[mQuoter.quoteIndex] + mQuoter.holding[mQuoter.quoteFarIndex]) : 0;
            }

            if (unhedgedOutrightPosition != 0)
            {
                return;
            }
            positionInfo.averagePrice = positionInfo.averageQuotePrice - positionInfo.averageLeanPrice;

            string positionInfoJson = JsonSerializer.Serialize(positionInfo, jsonOptions);
            File.WriteAllText(positionInfoFile, positionInfoJson);
        }

        private void ManagePendingOrders(int orderId, int amount)
        {
            mPendingBaseSpreadOrderAmounts[orderId] = amount;
            pendingBaseSpreadOrders += amount;
        }

        public void ManagePendingOrders(KGDeal deal)
        {
            if (mPendingBaseSpreadOrderAmounts.ContainsKey(deal.internalOrderNumber))
            {
                if (deal.isBuy)
                {
                    mPendingBaseSpreadOrderAmounts[deal.internalOrderNumber] -= deal.amount;
                }
                else
                {
                    mPendingBaseSpreadOrderAmounts[deal.internalOrderNumber] += deal.amount;
                }

                if (mPendingBaseSpreadOrderAmounts[deal.internalOrderNumber] == 0)
                {
                    mPendingBaseSpreadOrderAmounts.Remove(deal.internalOrderNumber);
                }

                pendingBaseSpreadOrders = 0;
                foreach (var perOrder in mPendingBaseSpreadOrderAmounts)
                {
                    pendingBaseSpreadOrders += perOrder.Value;
                }
            }
        }

        private void BuyIcs(int n, Detail.HedgeKind kind)
        {
            mOrders.SendOrder(mIcsBuy, mQuoter.icsIndex, Side.BUY, mQuoter.asks[mQuoter.icsIndex].price, n, "BASE_SPREADS");
            ManagePendingOrders(mIcsBuy.internalOrderNumber, n);
        }

        private void SellIcs(int n, Detail.HedgeKind kind)
        {
            mOrders.SendOrder(mIcsSell, mQuoter.icsIndex, Side.SELL, mQuoter.bids[mQuoter.icsIndex].price, n, "BASE_SPREADS");
            ManagePendingOrders(mIcsSell.internalOrderNumber, -n);
        }

        private int BuyLean(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(mLeanBuy, mQuoter.leanIndex, Side.BUY, mQuoter.asks[mQuoter.leanIndex].price, n, "BASE_SPREADS");
            ManagePendingOrders(orderId, n);
            return orderId;
        }

        private int SellLean(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(mLeanSell, mQuoter.leanIndex, Side.SELL, mQuoter.bids[mQuoter.leanIndex].price, n, "BASE_SPREADS");
            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int SellQuoted(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(mQuotedSell, mQuoter.quoteIndex, Side.SELL, mQuoter.bids[mQuoter.quoteIndex].price, n, "BASE_SPREADS");
            ManagePendingOrders(orderId, -n);
            return orderId;
        }

        private int BuyQuoted(int n, Detail.HedgeKind kind)
        {
            int orderId = mOrders.SendOrder(mQuotedBuy, mQuoter.quoteIndex, Side.BUY, mQuoter.asks[mQuoter.quoteIndex].price, n, "BASE_SPREADS");
            ManagePendingOrders(orderId, n);
            return orderId;
        }

        private void BuyBaseSpread(int n)
        {
            if (mQuoter.asks[mQuoter.icsIndex].price < mQuoter.asks[mQuoter.quoteIndex].price - mQuoter.bids[mQuoter.leanIndex].price)
            {
                BuyIcs(n, HedgeKind.BASE_SPREAD);
            }
            else if (Math.Abs(mQuoter.holding[mQuoter.leanIndex] + n) <= P.maxOutrights || mQuoter.holding[mQuoter.leanIndex] > -GetDesired() && mQuoter.holding[mQuoter.quoteIndex] < GetDesired())
            {
                BuyQuoted(n, HedgeKind.BASE_SPREAD);
                SellLean(n, HedgeKind.BASE_SPREAD);
            }

            pendingBaseSpreadOrders += n;
        }

        private void SellBaseSpread(int n)
        {
            if (mQuoter.bids[mQuoter.icsIndex].price > mQuoter.bids[mQuoter.quoteIndex].price - mQuoter.asks[mQuoter.leanIndex].price)
            {
                SellIcs(n, HedgeKind.BASE_SPREAD);
            }
            else if (Math.Abs(mQuoter.holding[mQuoter.leanIndex] - n) <= P.maxOutrights || mQuoter.holding[mQuoter.leanIndex] < -GetDesired() && mQuoter.holding[mQuoter.quoteIndex] > GetDesired())
            {
                SellQuoted(n, HedgeKind.BASE_SPREAD);
                BuyLean(n, HedgeKind.BASE_SPREAD);
            }

            pendingBaseSpreadOrders -= n;
        }

        private void GetBaseSpreadPrice()
        {
            double bidFromOutrights = mQuoter.bids[mQuoter.quoteIndex].price - mQuoter.asks[mQuoter.leanIndex].price;
            double askFromOutrights = mQuoter.asks[mQuoter.quoteIndex].price - mQuoter.bids[mQuoter.leanIndex].price;

            baseSpreadBid = Math.Max(mQuoter.bids[mQuoter.icsIndex].price, bidFromOutrights);
            baseSpreadAsk = Math.Min(mQuoter.asks[mQuoter.icsIndex].price, askFromOutrights);
        }
        public void ManageBaseSpreads()
        {
            if (pendingBaseSpreadOrders != 0 || mQuoter.hedging.pendingOutrightHedges != 0 || baseSpreadPosition == 0)
            {
                return;
            }

            GetBaseSpreadPrice();

            int volume = baseSpreadPosition - pendingBaseSpreadOrders;

            if (baseSpreadPosition < GetDesired() && baseSpreadAsk < positionInfo.averagePrice - P.creditOffset)
            {
                if (!mBaseSpreadThrottler.addTrade(volume))
                    return;

                BuyBaseSpread(volume);
                return;
            }

            if (baseSpreadPosition > GetDesired() && baseSpreadBid > positionInfo.averagePrice + P.creditOffset)
            {
                if (!mBaseSpreadThrottler.addTrade(volume))
                    return;

                SellBaseSpread(volume);
                return;
            }

            if (mQuoter.asks[mQuoter.quoteIndex].price - mQuoter.bids[mQuoter.leanIndex].price <= positionInfo.averagePrice && mQuoter.holding[mQuoter.leanIndex] > -GetDesired() && mQuoter.holding[mQuoter.quoteIndex] < GetDesired())
            {
                if (!mBaseSpreadThrottler.addTrade(volume))
                    return;

                BuyQuoted(volume, HedgeKind.BASE_SPREAD);
                SellLean(volume, HedgeKind.BASE_SPREAD);
                return;
            }

            if (mQuoter.bids[mQuoter.quoteIndex].price - mQuoter.asks[mQuoter.leanIndex].price >= positionInfo.averagePrice && mQuoter.holding[mQuoter.leanIndex] < -GetDesired() && mQuoter.holding[mQuoter.quoteIndex] > GetDesired())
            {
                if (!mBaseSpreadThrottler.addTrade(volume))
                    return;

                SellQuoted(volume, HedgeKind.BASE_SPREAD);
                BuyLean(volume, HedgeKind.BASE_SPREAD);
                return;
            }
        }

        public void GetPosition()
        {
            int ownPosition = mQuoter.holding[mQuoter.quoteIndex] + mQuoter.holding[mQuoter.quoteFarIndex];

            int icsPosition = 0;
            if (mQuoter.icsIndex != -1)
            {
                icsPosition = mQuoter.holding[mQuoter.icsIndex];
            }

            unhedgedOutrightPosition = ownPosition + mQuoter.holding[mQuoter.leanIndex];

            if (unhedgedOutrightPosition == 0)
            {
                baseSpreadPosition = ownPosition + icsPosition;
            }
        }
    }
}