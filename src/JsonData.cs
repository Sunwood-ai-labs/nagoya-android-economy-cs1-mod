using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NagoyaRoadDelivery
{
    public static class JsonData
    {
        public static object Parse(string text)
        {
            if(text==null || text.Length>8*1024*1024)throw new FormatException("JSON input size");
            Reader r=new Reader(text);object value=r.Value(0);r.White();if(r.Position!=text.Length)throw new FormatException("Trailing JSON content");return value;
        }
        public static string Stringify(object value) { StringBuilder b=new StringBuilder();Write(b,value,0);return b.ToString(); }
        private static void Write(StringBuilder b,object value,int depth)
        {
            if(depth>32)throw new FormatException("JSON depth");
            if(value==null){b.Append("null");return;}
            string text=value as string;
            if(text!=null){Quote(b,text);return;}
            if(value is bool){b.Append((bool)value?"true":"false");return;}
            IDictionary map=value as IDictionary;
            if(map!=null)
            {
                b.Append('{');bool first=true;foreach(DictionaryEntry item in map){if(!first)b.Append(',');first=false;Quote(b,(string)item.Key);b.Append(':');Write(b,item.Value,depth+1);}b.Append('}');return;
            }
            IList list=value as IList;
            if(list!=null){b.Append('[');for(int i=0;i<list.Count;i++){if(i>0)b.Append(',');Write(b,list[i],depth+1);}b.Append(']');return;}
            if(value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is float || value is double || value is decimal)
            {
                double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);if(double.IsNaN(number)||double.IsInfinity(number))throw new FormatException("Nonfinite JSON number");b.Append(Convert.ToString(value,CultureInfo.InvariantCulture));return;
            }
            throw new NotSupportedException("JSON data type: "+value.GetType());
        }
        private static void Quote(StringBuilder b,string text)
        {
            b.Append('"');foreach(char c in text)
            {
                if(c=='"')b.Append("\\\"");else if(c=='\\')b.Append("\\\\");else if(c=='\n')b.Append("\\n");else if(c=='\r')b.Append("\\r");else if(c=='\t')b.Append("\\t");
                else if(c<32)b.Append("\\u"+((int)c).ToString("x4"));else b.Append(c);
            }b.Append('"');
        }
        private sealed class Reader
        {
            private readonly string source;public int Position;
            public Reader(string text){source=text;}
            public void White(){for(;Position<source.Length && char.IsWhiteSpace(source[Position]);Position++){} }
            private char Take(){if(Position>=source.Length)throw new FormatException("Unexpected JSON end");return source[Position++];}
            public object Value(int depth)
            {
                if(depth>32)throw new FormatException("JSON depth");White();if(Position>=source.Length)throw new FormatException("Missing JSON value");char c=source[Position];
                if(c=='"')return Text();
                if(c=='{')
                {
                    Position++;Dictionary<string,object> map=new Dictionary<string,object>();White();if(Position<source.Length && source[Position]=='}'){Position++;return map;}
                    for(int count=0;count<100000;count++)
                    {
                        White();string key=Text();White();if(Take()!=':')throw new FormatException("Missing JSON colon");map.Add(key,Value(depth+1));White();char end=Take();if(end=='}')return map;if(end!=',')throw new FormatException("JSON object separator");
                    }throw new FormatException("JSON object size");
                }
                if(c=='[')
                {
                    Position++;List<object> list=new List<object>();White();if(Position<source.Length && source[Position]==']'){Position++;return list;}
                    for(int count=0;count<100000;count++){list.Add(Value(depth+1));White();char end=Take();if(end==']')return list;if(end!=',')throw new FormatException("JSON array separator");}throw new FormatException("JSON array size");
                }
                if(c=='t'){Literal("true");return true;}if(c=='f'){Literal("false");return false;}if(c=='n'){Literal("null");return null;}
                int start=Position;
                for(;Position<source.Length && "-+0123456789.eE".IndexOf(source[Position])>=0;Position++){}
                double n;if(start==Position || !double.TryParse(source.Substring(start,Position-start),NumberStyles.Float,CultureInfo.InvariantCulture,out n) || double.IsNaN(n) || double.IsInfinity(n))throw new FormatException("Invalid JSON number");return n;
            }
            private void Literal(string text){for(int i=0;i<text.Length;i++)if(Take()!=text[i])throw new FormatException("JSON literal");}
            private string Text()
            {
                if(Take()!='"')throw new FormatException("JSON string expected");StringBuilder b=new StringBuilder();
                for(int count=0;count<source.Length;count++)
                {
                    char c=Take();if(c=='"')return b.ToString();if(c<32)throw new FormatException("JSON string control character");
                    if(c!='\\'){b.Append(c);continue;}c=Take();
                    if(c=='"'||c=='\\'||c=='/')b.Append(c);else if(c=='b')b.Append('\b');else if(c=='f')b.Append('\f');else if(c=='n')b.Append('\n');else if(c=='r')b.Append('\r');else if(c=='t')b.Append('\t');
                    else if(c=='u'){if(Position+4>source.Length)throw new FormatException("Unicode escape");b.Append((char)int.Parse(source.Substring(Position,4),NumberStyles.HexNumber,CultureInfo.InvariantCulture));Position+=4;}
                    else throw new FormatException("JSON escape");
                }throw new FormatException("Unclosed JSON string");
            }
        }
    }
}
