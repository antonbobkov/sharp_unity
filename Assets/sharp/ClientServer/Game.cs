using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Xml.Serialization;
using System.ComponentModel;


namespace ServerClient
{
    [Serializable]
    public struct Point
    {
        public int x, y;

        public Point(int x_, int y_) { x = x_; y = y_; }
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("[{0}, {1}]", x, y);
            return sb.ToString();
        }

        public static Point operator +(Point p1, Point p2) { return new Point(p1.x + p2.x, p1.y + p2.y); }
        public static Point operator -(Point p1) { return new Point(-p1.x, -p1.y); }
        //public static bool operator ==(Point p1, Point p2) { return (p1.x == p2.x) && (p1.y == p2.y); }

        public static Point operator -(Point p1, Point p2) { return p1 + -p2; }
        //public static bool operator !=(Point p1, Point p2) { return !(p1 == p2); }

        public override bool Equals(object comparand) { return this.ToString().Equals(comparand.ToString()); }
        public override int GetHashCode() { return this.ToString().GetHashCode(); }

        public static bool operator ==(Point o1, Point o2) { return Object.Equals(o1, o2); }
        public static bool operator !=(Point o1, Point o2) { return !(o1 == o2); }

        public static IEnumerable<Point> Range(Point size)
        {
            Point p = new Point();
            for (p.y = 0; p.y < size.y; ++p.y)
                for (p.x = 0; p.x < size.x; ++p.x)
                    yield return p;
        }
        public static IEnumerable<Point> SymmetricRange(Point size)
        {
            Point p = new Point();
            for (p.y = -size.y; p.y <= size.y; ++p.y)
                for (p.x = -size.x; p.x <= size.x; ++p.x)
                    yield return p;
        }
    }


    [Serializable]
    public class Plane<T>
    {
        public T[] plane;
        public Point Size { get; set; }

        public Plane() { }
        public Plane(Point size) { plane = new T[size.x * size.y]; Size = size; }

        void AssertRange(int x, int y)
        {
            if(!InRange(x, y))
                throw new IndexOutOfRangeException(new StringBuilder().AppendFormat("Plane<{0}>{1} w/ size {2}", typeof(T).Name, new Point(x, y), Size).ToString());
        }

        public bool InRange(Point p)
        {
            return InRange(p.x, p.y);
        }

        public bool InRange(int x, int y)
        {
            if (x < 0 || x >= Size.x)
                return false;
            if (y < 0 || y >= Size.y)
                return false;
            return true;
        }

        public T this[Point pos]
        {
            get { return this[pos.x, pos.y]; }
            set { this[pos.x, pos.y] = value; }
        }
        public T this[int x, int y]
        {
            get { AssertRange(x, y);  return plane[y * Size.x + x]; }
            set { AssertRange(x, y);  plane[y * Size.x + x] = value; }
        }

        public IEnumerable<T> GetTiles()
        {
            foreach (Point p in Point.Range(Size))
                yield return this[p];
        }
        public IEnumerable<KeyValuePair<Point, T>> GetEnum()
        {
            foreach(Point p in Point.Range(Size))
                yield return new KeyValuePair<Point, T>(p, this[p]);
        }
    }
}
