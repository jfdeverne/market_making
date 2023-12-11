using System;
using System.Collections.Generic;
using KGClasses;

namespace StrategyRunner
{
    public class Orders
    {
        const int MAX_ORDER_SIZE = 1000;

        Strategy mStrategy;
        List<int /*internalOrderNumber*/> mPendingCancels;
        List<KGOrder> mToCancel;
        public Orders(Strategy strategy)
        {
            mStrategy = strategy;
            mPendingCancels = new List<int>();
            mToCancel = new List<KGOrder>();
        }

        private void Log(string message)
        {
            mStrategy.API.Log(String.Format("STG {0}: {1}", mStrategy.stgID, message));
            mStrategy.API.SendToRemote(message, KGConstants.EVENT_GENERAL_INFO);
        }

        public bool orderInUse(KGOrder order)
        {
            if ((order.orderStatus > 10 && (order.askSize >= 0 || order.bidSize >= 0)) || (order.orderStatus != 9) && (order.bidSize > 0 || order.askSize > 0))
            {
                return true;
            }
            return false;
        }

        public bool orderInTransientState(KGOrder order)
        {
            return (order.bidSize >= 0 || order.askSize >= 0) && order.orderStatus > 10;
        }

        private bool pricesAreEqual(double price1, double price2)
        {
            return Math.Abs(price1 - price2) < 1e-5;
        }

        public int SendOrder(KGOrder ord, int instrument, Detail.Side side, double price, int amount, string source, bool modify = false)
        {
            if (mStrategy.API.GetStrategyStatus(mStrategy.stgID) == 0 || mStrategy.systemTradingMode == 'C')
            {
                Log("ERR: attempt to send order while in cancel");
                return -1;
            }

            if (orderInUse(ord) && !modify)
            {
                return -1;
            }

            if (orderInTransientState(ord))
            {
                return -1;
            }

            int instrumentIndex = ord.index + mStrategy.API.n * ord.VenueID;

            bool isBuy = false;
            if (side == Detail.Side.BUY)
            {
                if (pricesAreEqual(ord.bid, price) && ord.bidSize == amount && instrumentIndex == instrument)
                {
                    return -1;
                }

                isBuy = true;
            }
            else if (side == Detail.Side.SELL)
            {
                if (pricesAreEqual(ord.ask, price) && ord.askSize == amount && instrumentIndex == instrument)
                {
                    return -1;
                }
            }

            if (amount <= 0)
            {
                Log("ERR: order amount has to be greater than zero");
                return -1;
            }

            if (amount > MAX_ORDER_SIZE)
            {
                Log("ERR: max order size exceeded");
                return -1;
            }

            mStrategy.API.Log(String.Format("[ORDERS] STG {0}: PopulateOrder instrument={1} side={2} price={3} amount={4} source={5}", mStrategy.stgID, instrument, side, price, amount, source));

            bool ret = mStrategy.API.PopulateOrder(ord, instrument, isBuy, price, amount, false);
            if (!ret)
            {
                mStrategy.API.Log("ERR: populating order in transient state" + instrument.ToString() + " " + price.ToString() + " " + amount.ToString());
                return -1;
            }
            ord.source = source;

            if (ord.orderStatus == 2)
                ord.orderStatus = 0;

            /*if (ord.VenueID == 1 && !mStrategy.eurexThrottler.addTrade())
            {
                Log(String.Format("max eurex order rate reached"));
                return -1;
            }*/

            mStrategy.API.PostOrder(ord, mStrategy.stgID);

            mStrategy.API.Log(String.Format("[ORDERS] STG {0}: SendOrder instrument={1} side={2} price={3} amount={4} source={5} id={6}", mStrategy.stgID, instrument, side, price, amount, source, ord.internalOrderNumber));

            return ord.internalOrderNumber;
        }

        public bool CancelOrder(KGOrder ord)
        {
            if (ord.orderStatus == 41 || ord.orderStatus == 4)
                return true;

            if (orderInTransientState(ord) || ord.orderStatus == 9)
            {
                mPendingCancels.Add(ord.internalOrderNumber);
                mStrategy.API.Log(String.Format("[ORDERS] CancelOrder PENDING status={0} order={1}", ord.orderStatus, ord.internalOrderNumber));
                return false;
            }

            mStrategy.API.Log(String.Format("[ORDERS] CancelOrder status={0} order={1}", ord.orderStatus, ord.internalOrderNumber));
            ord.orderStatus = 41;

            mStrategy.API.PostOrder(ord, mStrategy.stgID);
            mStrategy.API.Log(String.Format("[ORDERS] CancelOrder Post status={0} order={1}", ord.orderStatus, ord.internalOrderNumber));

            /*if (ord.VenueID == 1 && !mStrategy.eurexThrottler.addTrade())
            {
                Log(String.Format("max eurex order rate reached, allowing cancellation"));
            }*/
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

        public KGOrder GetOrderById(int id)
        {
            foreach (var order in mStrategy.strategyOrders)
            {
                if (order.internalOrderNumber == id)
                {
                    return order;
                }
            }

            return null;
        }

        public void CheckPendingCancels()
        {
            foreach (var ord in mStrategy.strategyOrders)
            {
                if (orderInTransientState(ord))
                    continue;

                if (mPendingCancels.Contains(ord.internalOrderNumber))
                {
                    if (ord.orderStatus == 9 || ord.orderStatus == 4)
                    {
                        mPendingCancels.Remove(ord.internalOrderNumber);
                        return;
                    }

                    mStrategy.API.Log(String.Format("STG {0}: pending cancel retry for order {1}", mStrategy.stgID, ord.internalOrderNumber));
                    CancelOnNextMD(ord);
                    mPendingCancels.Remove(ord.internalOrderNumber);
                }
            }
        }

        public void CancelOnNextMD(KGOrder order)
        {
            mToCancel.Add(order);
        }

        public void OnProcessMD()
        {
            List<KGOrder> ordersToRemove = new List<KGOrder>();
            foreach (var order in mToCancel)
            {
                CancelOrder(order);
                ordersToRemove.Add(order);
            }
            foreach (var order in ordersToRemove)
            {
                mToCancel.Remove(order);
            }
        }
    }
}