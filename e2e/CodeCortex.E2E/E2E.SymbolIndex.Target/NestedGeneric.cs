namespace G {
    public class Outer<T> {
        public class Inner { }
        public class InnerGeneric<U> { }
    }
    public class Outer2<T1, T2> {
        public class Inner { }
    }
    public class OuterNonGeneric {
        public class Inner2<T> { }
    }
}

