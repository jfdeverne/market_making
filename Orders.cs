using System;
using System.Collections.Generic;
using KGClasses;

namespace StrategyRunner
{
    public class Orders
    {
        Strategy mStrategy;
        List<int /*internalOrderNumber*/> mPendingCancels;
        public Orders(Strategy strategy)
        {
            mStrategy = strategy;
            mPendingCancels = new List<int>();
        }

        private void Log(string message)
        {
            mStrategy.API.Log(String.Format("STG {0}: {1}", mStrategy.stgID, message));
            mStrategy.API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        public bool orderInUse(KGOrder order)
        {
            if ((order.orderStatus > 10 && (order.askSize >= 0 || order.bidSize >= 0)) || order.bidSize > 0 || order.askSize > 0)
            {
                return true;
            }
            return false;
        }

        public bool orderInTransientState(KGOrder order)
        {
            return (order.bidSize >= 0 || order.askSize >= 0) && order.orderStatus > 10;
        }

        public int SendOrder(KGOrder ord, int instrument, Detail.Side side, double price, int amount, string source)
        {
            if (mStrategy.API.GetStrategyStatus(mStrategy.stgID) == 0 || mStrategy.systemTradingMode == 'C')
            {
                Log("ERR: attempt to send order while in cancel");
                return -1;
            }

            if (orderInUse(ord))
            {
                Log("ERR: SendOrder failed, order in use");
                return -1;
            }

            if (orderInTransientState(ord))
            {
                Log("ERR: SendOrder failed, order in transient state");
                return -1;
            }

            bool isBuy = false;
            if (side == Detail.Side.BUY)
            {
                isBuy = true;
            }

            if (amount <= 0)
            {
                Log("ERR: order amount has to be greater than zero");
                return -1;
            }

            mStrategy.API.PopulateOrder(ord, instrument, isBuy, price, amount, false);
            ord.source = source;
            mStrategy.API.PostOrder(ord, mStrategy.stgID);

            return ord.internalOrderNumber;
        }

        public void OnOrder(KGOrder ord)
        {
            if (mPendingCancels.Contains(ord.internalOrderNumber))
            {
                CancelOrder(ord);
                mPendingCancels.Remove(ord.internalOrderNumber);
                Log(String.Format("pending cancel: cancel order {0}", ord.internalOrderNumber));
            }
        }

        public bool CancelOrder(KGOrder ord)
        {
            if (orderInTransientState(ord))
            {
                //Log("ERR: CancelOrder failed, order in transient state");
                mPendingCancels.Add(ord.internalOrderNumber);
                Log(String.Format("pending cancel: transient order {0}", ord.internalOrderNumber));
                return false;
            }

            ord.orderStatus = 41;
            mStrategy.API.PostOrder(ord, mStrategy.stgID);
            return true;
        }

        public void CancelOrder(int id)
        {
            foreach (var order in mStrategy.strategyOrders)
            {
                if (order.internalOrderNumber == id)
                {
                    CancelOrder(order);
                }
            }
        }
    }
}

