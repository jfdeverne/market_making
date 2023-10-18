﻿using System;
using System.Text.RegularExpressions;
using System.Reflection;

namespace StrategyRunner
{   
    public static class P
    {
        public static double eus1 = 0;
        public static double eus2 = 0;
        public static double eus3 = 0;
        public static double eus4 = 0;
        public static double eus5 = 0;
        public static double eus6 = 0;

        public static int maxTradeSize = 0;

        public static int maxOutrights = 0;

        public static double joinFactor = 0;

        public static int desiredEus1 = 0;
        public static int desiredEus2 = 0;
        public static int desiredEus3 = 0;
        public static int desiredEus4 = 0;
        public static int desiredEus5 = 0;
        public static int desiredEus6 = 0;

        public static double creditOffset = 0;

        public static int baseSpreadThrottleVolume = 0;
        public static int baseSpreadThrottleSeconds = 0;

        public static int maxCrossVolume = 0;

        public static bool bvEnabled = false;

        public static int bvThrottleVolume = 0;
        public static int bvThrottleSeconds = 0;

        public static double bvTimeoutSeconds = 0;
        public static double bvMaxLoss = 0;

        public static double spreaderTargetPrice = 0;
        public static int spreaderStepSize = 10;

        public static string GetParamsStr()
        {
            string line="";
            foreach (FieldInfo field in typeof(P).GetFields())
            {
                line += " " + field.Name.ToString() + "=" + field.GetValue(null).ToString();
            }
            return line;
        }

        public static (bool, string) SetValue(string paramName, string paramValue)
        {
            string ret = "";
            bool found = false;
            bool valueChanged = false;
            foreach (FieldInfo field in typeof(P).GetFields())
            {               
                if (field.Name != paramName)
                    continue;
                else
                {
                    found = true;                    
                    if (field.FieldType == typeof(int))
                    {
                        int val = Int32.Parse(paramValue);
                        valueChanged = val != (int)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    else if (field.FieldType == typeof(string))
                    {
                        valueChanged = paramValue != (string)field.GetValue(null);
                        field.SetValue(null, paramValue);
                    }
                    else if (field.FieldType == typeof(double))
                    {
                        double val = Double.Parse(paramValue);
                        valueChanged = val != (double)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    else if (field.FieldType == typeof(bool))
                    {
                        if (paramValue == "true")
                        {
                            valueChanged = !(bool)field.GetValue(null);
                            field.SetValue(null, true);
                        }
                        else
                        {
                            valueChanged = (bool)field.GetValue(null);
                            field.SetValue(null, false);
                        }
                    }
                    else if (field.FieldType == typeof(long))
                    {
                        long val = long.Parse(paramValue);
                        valueChanged = val != (long)field.GetValue(null);
                        field.SetValue(null, val);
                    }
                    break;
                } //end else
            } // end foreach
            if (!found)
                ret += "::UPDATEPARAMS ERR - Parameter " + paramName + " isn't defined in KG";
           
            return (valueChanged, ret);
        }

        public static string SetValues(string strMessage)
        {
            string ret = "";
            string first = "";
          
            char[] seps = { '=' };
            Regex regex = new Regex(@"\s*\S+\s*");
            MatchCollection matchCollection = regex.Matches(strMessage);
            
            for (int i = 0; i < matchCollection.Count; i++)
            {
                bool found = false;
                string[] parts = (matchCollection[i].Value.Trim()).Split(seps);
                if (parts.Length != 2)
                {
                    ret = "Parameters formatting error";                    
                    return ret;
                }
                SetValue(parts[0].Trim(), parts[1].Trim());
                //foreach (FieldInfo field in typeof(P).GetFields())
                //{
                //    first = parts[0].Trim();
                //    if (field.Name != first)
                //        continue;
                //    else
                //    {
                //        found = true;
                //        string second = parts[1].Trim();                        
                //        if (field.FieldType == typeof(int))
                //        {
                //            int val = Int32.Parse(second);
                //            field.SetValue(null, val);
                //        }
                //        else if (field.FieldType == typeof(string))
                //        {
                //            field.SetValue(null, second);
                //        }
                //        else if (field.FieldType == typeof(double))
                //        {
                //            double val = Double.Parse(second);
                //            field.SetValue(null, val);
                //        }
                //        else if (field.FieldType == typeof(bool))
                //        {
                //            if (second == "true")
                //                field.SetValue(null, true);
                //            else
                //                field.SetValue(null, false);
                //        }
                //        else if (field.FieldType == typeof(long))
                //        {
                //            long val = long.Parse(second);
                //            field.SetValue(null, val);                            
                //        }
                //        break;
                //    } //end else
                //} // end foreach

                //if (!found)
                //    ret += "::UPDATEPARAMS ERR - Parameter " + first + " isn't defined in KG";
            } //end -for matchCollection
                     
            return ret;

        }//end SetValues
    }
}
