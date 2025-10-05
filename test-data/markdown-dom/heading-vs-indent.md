在Markdig中，会把`# C1.1.1`解析为`## C1.1`的平级兄弟Block。Heading语义优先于缩进语义。

# C1
A

## C1.1
B
  # C1.1.1
  C

## C1.2
D

# C2
E
