namespace Demo.Unicode;

// UTF-8 注释用于冻结字节与行列位置：你好 😀
public class 格式化器<T>
{
    public int Format(int value) => value;
    public string Format(string value) => value;
    public T Echo<TValue>(TValue value) => default!;
}

public class Secondary
{
    public int Format(int value) => value;
}
