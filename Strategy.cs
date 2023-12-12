using KGClasses;
using StrategyLib;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace StrategyRunner
{
    public abstract class Strategy
    {
        public abstract void OnSystemTradingMode(ref char c);
        public abstract void OnPurgedOrder(KGOrder ord);
        public abstract void OnStatusChanged(int status);
        public abstract void OnFlush();
        public abstract void OnOrder(KGOrder ord);
        public abstract void OnProcessMD(VIT vi);
        public abstract void OnDeal(KGDeal deal);
        public abstract void OnParamsUpdate(string paramName, string paramValue);
        public abstract void OnGlobalParamsUpdate();
        public abstract int GetNetPosition();

        public abstract double GetMaxLossMarketHedge();
        public abstract double GetMaxLossLimitHedge();

        public int quoteIndex;
        public int farIndex;
        public int leanIndex;
        public double boxTargetPrice; //"BOX" REFERS TO (quoteIndex - leanIndex)
        public double correlatedSpreadTargetPrice; //REFERS TO (quoteIndex - correlatedMarketIndex)
        public double crossSpreadTargetPrice; //REFERS TO (quoteIndex - crossVenueIndex), FOR NOW ==0
        public double nearSpreadTargetPrice; //REFERS TO (quoteIndex - (quoteIndex+1)), UNUSED FOR NOW

        public int[] holding;
        public DepthElement[] bids;
        public DepthElement[] asks;

        public List<int> crossVenueIndices;
        public List<int> correlatedIndices;

        public List<KGOrder> strategyOrders;

        public API API;
        public int stgID;
        public char systemTradingMode = 'C';
        public Orders orders;

        public int linkedBoxIndex = -1;

        public int limitPlusSize;
        public int nonLeanLimitPlusSize;

        public double tickSize;

        public static double maxLossMarketHedge = -1;
        public static double maxLossLimitHedge = -1;

        public static double eurexThrottleSeconds = -1;
        public static int eurexThrottleVolume = -1;

        public Throttler.EurexThrottler eurexThrottler;
    }
}
