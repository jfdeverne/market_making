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
        public abstract void OnImprovedCM(int index, double CMPrice);
        public abstract void OnDeal(KGDeal deal);
        public abstract void OnParamsUpdate();

        public int quoteIndex;
        public int quoteFarIndex;
        public int leanIndex;

        public int[] holding;
        public DepthElement[] bids;
        public DepthElement[] asks;
        public double[] theos;

        public List<KGOrder> strategyOrders;

        public API API;
        public int stgID;
        public char systemTradingMode;
        public Orders orders;
        public bool activeStopOrders;

    }
}
