using KGClasses;
using StrategyLib;
using System.Collections.Generic;

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
        public abstract int GetNetPosition();

        public int quoteIndex;
        public int farIndex;
        public int leanIndex;
        public double boxTargetPrice; //"BOX" REFERS TO (quoteIndex - leanIndex)

        public int[] holding;
        public DepthElement[] bids;
        public DepthElement[] asks;

        public List<KGOrder> strategyOrders;

        public API API;
        public int stgID;
        public char systemTradingMode = 'C';
        public Orders orders;
        public bool activeStopOrders;

        public int linkedBoxIndex = -1;

        public int limitPlusSize;

        public double tickSize;
    }
}
