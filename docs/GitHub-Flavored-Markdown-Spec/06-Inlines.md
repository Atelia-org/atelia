# Inlines

Inlines are parsed sequentially from the beginning of the character
stream to the end (left to right, in left-to-right languages).
Thus, for example, in

```````````````````````````````` example
`hi`lo`
.
<p><code>hi</code>lo`</p>
````````````````````````````````

`hi` is parsed as code, leaving the backtick at the end as a literal
backtick.


## Backslash escapes

Any ASCII punctuation character may be backslash-escaped:

```````````````````````````````` example
\!\"\#\$\%\&\'\(\)\*\+\,\-\.\/\:\;\<\=\>\?\@\[\\\]\^\_\`\{\|\}\~
.
<p>!&quot;#$%&amp;'()*+,-./:;&lt;=&gt;?@[\]^_`{|}~</p>
````````````````````````````````


Backslashes before other characters are treated as literal
backslashes:

```````````````````````````````` example
\→\A\a\ \3\φ\«
.
<p>\→\A\a\ \3\φ\«</p>
````````````````````````````````


Escaped characters are treated as regular characters and do
not have their usual Markdown meanings:

```````````````````````````````` example
\*not emphasized*
\<br/> not a tag
\[not a link](/foo)
\`not code`
1\. not a list
\* not a list
\# not a heading
\[foo]: /url "not a reference"
\&ouml; not a character entity
.
<p>*not emphasized*
&lt;br/&gt; not a tag
[not a link](/foo)
`not code`
1. not a list
* not a list
# not a heading
[foo]: /url &quot;not a reference&quot;
&amp;ouml; not a character entity</p>
````````````````````````````````


If a backslash is itself escaped, the following character is not:

```````````````````````````````` example
\\*emphasis*
.
<p>\<em>emphasis</em></p>
````````````````````````````````


A backslash at the end of the line is a [hard line break]:

```````````````````````````````` example
foo\
bar
.
<p>foo<br />
bar</p>
````````````````````````````````


Backslash escapes do not work in code blocks, code spans, autolinks, or
raw HTML:

```````````````````````````````` example
`` \[\` ``
.
<p><code>\[\`</code></p>
````````````````````````````````


```````````````````````````````` example
    \[\]
.
<pre><code>\[\]
</code></pre>
````````````````````````````````


```````````````````````````````` example
~~~
\[\]
~~~
.
<pre><code>\[\]
</code></pre>
````````````````````````````````


```````````````````````````````` example
<http://example.com?find=\*>
.
<p><a href="http://example.com?find=%5C*">http://example.com?find=\*</a></p>
````````````````````````````````


```````````````````````````````` example
<a href="/bar\/)">
.
<a href="/bar\/)">
````````````````````````````````


But they work in all other contexts, including URLs and link titles,
link references, and [info strings] in [fenced code blocks]:

```````````````````````````````` example
[foo](/bar\* "ti\*tle")
.
<p><a href="/bar*" title="ti*tle">foo</a></p>
````````````````````````````````


```````````````````````````````` example
[foo]

[foo]: /bar\* "ti\*tle"
.
<p><a href="/bar*" title="ti*tle">foo</a></p>
````````````````````````````````


```````````````````````````````` example
``` foo\+bar
foo
```
.
<pre><code class="language-foo+bar">foo
</code></pre>
````````````````````````````````



## Entity and numeric character references

Valid HTML entity references and numeric character references
can be used in place of the corresponding Unicode character,
with the following exceptions:

- Entity and character references are not recognized in code
  blocks and code spans.

- Entity and character references cannot stand in place of
  special characters that define structural elements in
  CommonMark.  For example, although `&#42;` can be used
  in place of a literal `*` character, `&#42;` cannot replace
  `*` in emphasis delimiters, bullet list markers, or thematic
  breaks.

Conforming CommonMark parsers need not store information about
whether a particular character was represented in the source
using a Unicode character or an entity reference.

[Entity references](@) consist of `&` + any of the valid
HTML5 entity names + `;`. The
document <https://html.spec.whatwg.org/multipage/entities.json>
is used as an authoritative source for the valid entity
references and their corresponding code points.

```````````````````````````````` example
&nbsp; &amp; &copy; &AElig; &Dcaron;
&frac34; &HilbertSpace; &DifferentialD;
&ClockwiseContourIntegral; &ngE;
.
<p>  &amp; © Æ Ď
¾ ℋ ⅆ
∲ ≧̸</p>
````````````````````````````````


[Decimal numeric character
references](@)
consist of `&#` + a string of 1--7 arabic digits + `;`. A
numeric character reference is parsed as the corresponding
Unicode character. Invalid Unicode code points will be replaced by
the REPLACEMENT CHARACTER (`U+FFFD`).  For security reasons,
the code point `U+0000` will also be replaced by `U+FFFD`.

```````````````````````````````` example
&#35; &#1234; &#992; &#0;
.
<p># Ӓ Ϡ �</p>
````````````````````````````````


[Hexadecimal numeric character
references](@) consist of `&#` +
either `X` or `x` + a string of 1-6 hexadecimal digits + `;`.
They too are parsed as the corresponding Unicode character (this
time specified with a hexadecimal numeral instead of decimal).

```````````````````````````````` example
&#X22; &#XD06; &#xcab;
.
<p>&quot; ആ ಫ</p>
````````````````````````````````


Here are some nonentities:

```````````````````````````````` example
&nbsp &x; &#; &#x;
&#987654321;
&#abcdef0;
&ThisIsNotDefined; &hi?;
.
<p>&amp;nbsp &amp;x; &amp;#; &amp;#x;
&amp;#987654321;
&amp;#abcdef0;
&amp;ThisIsNotDefined; &amp;hi?;</p>
````````````````````````````````


Although HTML5 does accept some entity references
without a trailing semicolon (such as `&copy`), these are not
recognized here, because it makes the grammar too ambiguous:

```````````````````````````````` example
&copy
.
<p>&amp;copy</p>
````````````````````````````````


Strings that are not on the list of HTML5 named entities are not
recognized as entity references either:

```````````````````````````````` example
&MadeUpEntity;
.
<p>&amp;MadeUpEntity;</p>
````````````````````````````````


Entity and numeric character references are recognized in any
context besides code spans or code blocks, including
URLs, [link titles], and [fenced code block][] [info strings]:

```````````````````````````````` example
<a href="&ouml;&ouml;.html">
.
<a href="&ouml;&ouml;.html">
````````````````````````````````


```````````````````````````````` example
[foo](/f&ouml;&ouml; "f&ouml;&ouml;")
.
<p><a href="/f%C3%B6%C3%B6" title="föö">foo</a></p>
````````````````````````````````


```````````````````````````````` example
[foo]

[foo]: /f&ouml;&ouml; "f&ouml;&ouml;"
.
<p><a href="/f%C3%B6%C3%B6" title="föö">foo</a></p>
````````````````````````````````


```````````````````````````````` example
``` f&ouml;&ouml;
foo
```
.
<pre><code class="language-föö">foo
</code></pre>
````````````````````````````````


Entity and numeric character references are treated as literal
text in code spans and code blocks:

```````````````````````````````` example
`f&ouml;&ouml;`
.
<p><code>f&amp;ouml;&amp;ouml;</code></p>
````````````````````````````````


```````````````````````````````` example
    f&ouml;f&ouml;
.
<pre><code>f&amp;ouml;f&amp;ouml;
</code></pre>
````````````````````````````````


Entity and numeric character references cannot be used
in place of symbols indicating structure in CommonMark
documents.

```````````````````````````````` example
&#42;foo&#42;
*foo*
.
<p>*foo*
<em>foo</em></p>
````````````````````````````````

```````````````````````````````` example
&#42; foo

* foo
.
<p>* foo</p>
<ul>
<li>foo</li>
</ul>
````````````````````````````````

```````````````````````````````` example
foo&#10;&#10;bar
.
<p>foo

bar</p>
````````````````````````````````

```````````````````````````````` example
&#9;foo
.
<p>→foo</p>
````````````````````````````````


```````````````````````````````` example
[a](url &quot;tit&quot;)
.
<p>[a](url &quot;tit&quot;)</p>
````````````````````````````````


## Code spans

A [backtick string](@)
is a string of one or more backtick characters (`` ` ``) that is neither
preceded nor followed by a backtick.

A [code span](@) begins with a backtick string and ends with
a backtick string of equal length.  The contents of the code span are
the characters between the two backtick strings, normalized in the
following ways:

- First, [line endings] are converted to [spaces].
- If the resulting string both begins *and* ends with a [space]
  character, but does not consist entirely of [space]
  characters, a single [space] character is removed from the
  front and back.  This allows you to include code that begins
  or ends with backtick characters, which must be separated by
  whitespace from the opening or closing backtick strings.

This is a simple code span:

```````````````````````````````` example
`foo`
.
<p><code>foo</code></p>
````````````````````````````````


Here two backticks are used, because the code contains a backtick.
This example also illustrates stripping of a single leading and
trailing space:

```````````````````````````````` example
`` foo ` bar ``
.
<p><code>foo ` bar</code></p>
````````````````````````````````


This example shows the motivation for stripping leading and trailing
spaces:

```````````````````````````````` example
` `` `
.
<p><code>``</code></p>
````````````````````````````````

Note that only *one* space is stripped:

```````````````````````````````` example
`  ``  `
.
<p><code> `` </code></p>
````````````````````````````````

The stripping only happens if the space is on both
sides of the string:

```````````````````````````````` example
` a`
.
<p><code> a</code></p>
````````````````````````````````

Only [spaces], and not [unicode whitespace] in general, are
stripped in this way:

```````````````````````````````` example
` b `
.
<p><code> b </code></p>
````````````````````````````````

No stripping occurs if the code span contains only spaces:

```````````````````````````````` example
` `
`  `
.
<p><code> </code>
<code>  </code></p>
````````````````````````````````


[Line endings] are treated like spaces:

```````````````````````````````` example
``
foo
bar
baz
``
.
<p><code>foo bar   baz</code></p>
````````````````````````````````

```````````````````````````````` example
``
foo
``
.
<p><code>foo </code></p>
````````````````````````````````


Interior spaces are not collapsed:

```````````````````````````````` example
`foo   bar
baz`
.
<p><code>foo   bar  baz</code></p>
````````````````````````````````

Note that browsers will typically collapse consecutive spaces
when rendering `<code>` elements, so it is recommended that
the following CSS be used:

    code{white-space: pre-wrap;}


Note that backslash escapes do not work in code spans. All backslashes
are treated literally:

```````````````````````````````` example
`foo\`bar`
.
<p><code>foo\</code>bar`</p>
````````````````````````````````


Backslash escapes are never needed, because one can always choose a
string of *n* backtick characters as delimiters, where the code does
not contain any strings of exactly *n* backtick characters.

```````````````````````````````` example
``foo`bar``
.
<p><code>foo`bar</code></p>
````````````````````````````````

```````````````````````````````` example
` foo `` bar `
.
<p><code>foo `` bar</code></p>
````````````````````````````````


Code span backticks have higher precedence than any other inline
constructs except HTML tags and autolinks.  Thus, for example, this is
not parsed as emphasized text, since the second `*` is part of a code
span:

```````````````````````````````` example
*foo`*`
.
<p>*foo<code>*</code></p>
````````````````````````````````


And this is not parsed as a link:

```````````````````````````````` example
[not a `link](/foo`)
.
<p>[not a <code>link](/foo</code>)</p>
````````````````````````````````


Code spans, HTML tags, and autolinks have the same precedence.
Thus, this is code:

```````````````````````````````` example
`<a href="`">`
.
<p><code>&lt;a href=&quot;</code>&quot;&gt;`</p>
````````````````````````````````


But this is an HTML tag:

```````````````````````````````` example
<a href="`">`
.
<p><a href="`">`</p>
````````````````````````````````


And this is code:

```````````````````````````````` example
`<http://foo.bar.`baz>`
.
<p><code>&lt;http://foo.bar.</code>baz&gt;`</p>
````````````````````````````````


But this is an autolink:

```````````````````````````````` example
<http://foo.bar.`baz>`
.
<p><a href="http://foo.bar.%60baz">http://foo.bar.`baz</a>`</p>
````````````````````````````````


When a backtick string is not closed by a matching backtick string,
we just have literal backticks:

```````````````````````````````` example
```foo``
.
<p>```foo``</p>
````````````````````````````````


```````````````````````````````` example
`foo
.
<p>`foo</p>
````````````````````````````````

The following case also illustrates the need for opening and
closing backtick strings to be equal in length:

```````````````````````````````` example
`foo``bar``
.
<p>`foo<code>bar</code></p>
````````````````````````````````


## Emphasis and strong emphasis

John Gruber's original [Markdown syntax
description](http://daringfireball.net/projects/markdown/syntax#em) says:

> Markdown treats asterisks (`*`) and underscores (`_`) as indicators of
> emphasis. Text wrapped with one `*` or `_` will be wrapped with an HTML
> `<em>` tag; double `*`'s or `_`'s will be wrapped with an HTML `<strong>`
> tag.

This is enough for most users, but these rules leave much undecided,
especially when it comes to nested emphasis.  The original
`Markdown.pl` test suite makes it clear that triple `***` and
`___` delimiters can be used for strong emphasis, and most
implementations have also allowed the following patterns:

``` markdown
***strong emph***
***strong** in emph*
***emph* in strong**
**in strong *emph***
*in emph **strong***
```

The following patterns are less widely supported, but the intent
is clear and they are useful (especially in contexts like bibliography
entries):

``` markdown
*emph *with emph* in it*
**strong **with strong** in it**
```

Many implementations have also restricted intraword emphasis to
the `*` forms, to avoid unwanted emphasis in words containing
internal underscores.  (It is best practice to put these in code
spans, but users often do not.)

``` markdown
internal emphasis: foo*bar*baz
no emphasis: foo_bar_baz
```

The rules given below capture all of these patterns, while allowing
for efficient parsing strategies that do not backtrack.

First, some definitions.  A [delimiter run](@) is either
a sequence of one or more `*` characters that is not preceded or
followed by a non-backslash-escaped `*` character, or a sequence
of one or more `_` characters that is not preceded or followed by
a non-backslash-escaped `_` character.

A [left-flanking delimiter run](@) is
a [delimiter run] that is (1) not followed by [Unicode whitespace],
and either (2a) not followed by a [punctuation character], or
(2b) followed by a [punctuation character] and
preceded by [Unicode whitespace] or a [punctuation character].
For purposes of this definition, the beginning and the end of
the line count as Unicode whitespace.

A [right-flanking delimiter run](@) is
a [delimiter run] that is (1) not preceded by [Unicode whitespace],
and either (2a) not preceded by a [punctuation character], or
(2b) preceded by a [punctuation character] and
followed by [Unicode whitespace] or a [punctuation character].
For purposes of this definition, the beginning and the end of
the line count as Unicode whitespace.

Here are some examples of delimiter runs.

  - left-flanking but not right-flanking:

    ```
    ***abc
      _abc
    **"abc"
     _"abc"
    ```

  - right-flanking but not left-flanking:

    ```
     abc***
     abc_
    "abc"**
    "abc"_
    ```

  - Both left and right-flanking:

    ```
     abc***def
    "abc"_"def"
    ```

  - Neither left nor right-flanking:

    ```
    abc *** def
    a _ b
    ```

(The idea of distinguishing left-flanking and right-flanking
delimiter runs based on the character before and the character
after comes from Roopesh Chander's
[vfmd](http://www.vfmd.org/vfmd-spec/specification/#procedure-for-identifying-emphasis-tags).
vfmd uses the terminology "emphasis indicator string" instead of "delimiter
run," and its rules for distinguishing left- and right-flanking runs
are a bit more complex than the ones given here.)

The following rules define emphasis and strong emphasis:

1.  A single `*` character [can open emphasis](@)
    iff (if and only if) it is part of a [left-flanking delimiter run].

2.  A single `_` character [can open emphasis] iff
    it is part of a [left-flanking delimiter run]
    and either (a) not part of a [right-flanking delimiter run]
    or (b) part of a [right-flanking delimiter run]
    preceded by punctuation.

3.  A single `*` character [can close emphasis](@)
    iff it is part of a [right-flanking delimiter run].

4.  A single `_` character [can close emphasis] iff
    it is part of a [right-flanking delimiter run]
    and either (a) not part of a [left-flanking delimiter run]
    or (b) part of a [left-flanking delimiter run]
    followed by punctuation.

5.  A double `**` [can open strong emphasis](@)
    iff it is part of a [left-flanking delimiter run].

6.  A double `__` [can open strong emphasis] iff
    it is part of a [left-flanking delimiter run]
    and either (a) not part of a [right-flanking delimiter run]
    or (b) part of a [right-flanking delimiter run]
    preceded by punctuation.

7.  A double `**` [can close strong emphasis](@)
    iff it is part of a [right-flanking delimiter run].

8.  A double `__` [can close strong emphasis] iff
    it is part of a [right-flanking delimiter run]
    and either (a) not part of a [left-flanking delimiter run]
    or (b) part of a [left-flanking delimiter run]
    followed by punctuation.

9.  Emphasis begins with a delimiter that [can open emphasis] and ends
    with a delimiter that [can close emphasis], and that uses the same
    character (`_` or `*`) as the opening delimiter.  The
    opening and closing delimiters must belong to separate
    [delimiter runs].  If one of the delimiters can both
    open and close emphasis, then the sum of the lengths of the
    delimiter runs containing the opening and closing delimiters
    must not be a multiple of 3 unless both lengths are
    multiples of 3.

10. Strong emphasis begins with a delimiter that
    [can open strong emphasis] and ends with a delimiter that
    [can close strong emphasis], and that uses the same character
    (`_` or `*`) as the opening delimiter.  The
    opening and closing delimiters must belong to separate
    [delimiter runs].  If one of the delimiters can both open
    and close strong emphasis, then the sum of the lengths of
    the delimiter runs containing the opening and closing
    delimiters must not be a multiple of 3 unless both lengths
    are multiples of 3.

11. A literal `*` character cannot occur at the beginning or end of
    `*`-delimited emphasis or `**`-delimited strong emphasis, unless it
    is backslash-escaped.

12. A literal `_` character cannot occur at the beginning or end of
    `_`-delimited emphasis or `__`-delimited strong emphasis, unless it
    is backslash-escaped.

Where rules 1--12 above are compatible with multiple parsings,
the following principles resolve ambiguity:

13. The number of nestings should be minimized. Thus, for example,
    an interpretation `<strong>...</strong>` is always preferred to
    `<em><em>...</em></em>`.

14. An interpretation `<em><strong>...</strong></em>` is always
    preferred to `<strong><em>...</em></strong>`.

15. When two potential emphasis or strong emphasis spans overlap,
    so that the second begins before the first ends and ends after
    the first ends, the first takes precedence. Thus, for example,
    `*foo _bar* baz_` is parsed as `<em>foo _bar</em> baz_` rather
    than `*foo <em>bar* baz</em>`.

16. When there are two potential emphasis or strong emphasis spans
    with the same closing delimiter, the shorter one (the one that
    opens later) takes precedence. Thus, for example,
    `**foo **bar baz**` is parsed as `**foo <strong>bar baz</strong>`
    rather than `<strong>foo **bar baz</strong>`.

17. Inline code spans, links, images, and HTML tags group more tightly
    than emphasis.  So, when there is a choice between an interpretation
    that contains one of these elements and one that does not, the
    former always wins.  Thus, for example, `*[foo*](bar)` is
    parsed as `*<a href="bar">foo*</a>` rather than as
    `<em>[foo</em>](bar)`.

These rules can be illustrated through a series of examples.

Rule 1:

```````````````````````````````` example
*foo bar*
.
<p><em>foo bar</em></p>
````````````````````````````````


This is not emphasis, because the opening `*` is followed by
whitespace, and hence not part of a [left-flanking delimiter run]:

```````````````````````````````` example
a * foo bar*
.
<p>a * foo bar*</p>
````````````````````````````````


This is not emphasis, because the opening `*` is preceded
by an alphanumeric and followed by punctuation, and hence
not part of a [left-flanking delimiter run]:

```````````````````````````````` example
a*"foo"*
.
<p>a*&quot;foo&quot;*</p>
````````````````````````````````


Unicode nonbreaking spaces count as whitespace, too:

```````````````````````````````` example
* a *
.
<p>* a *</p>
````````````````````````````````


Intraword emphasis with `*` is permitted:

```````````````````````````````` example
foo*bar*
.
<p>foo<em>bar</em></p>
````````````````````````````````


```````````````````````````````` example
5*6*78
.
<p>5<em>6</em>78</p>
````````````````````````````````


Rule 2:

```````````````````````````````` example
_foo bar_
.
<p><em>foo bar</em></p>
````````````````````````````````


This is not emphasis, because the opening `_` is followed by
whitespace:

```````````````````````````````` example
_ foo bar_
.
<p>_ foo bar_</p>
````````````````````````````````


This is not emphasis, because the opening `_` is preceded
by an alphanumeric and followed by punctuation:

```````````````````````````````` example
a_"foo"_
.
<p>a_&quot;foo&quot;_</p>
````````````````````````````````


Emphasis with `_` is not allowed inside words:

```````````````````````````````` example
foo_bar_
.
<p>foo_bar_</p>
````````````````````````````````


```````````````````````````````` example
5_6_78
.
<p>5_6_78</p>
````````````````````````````````


```````````````````````````````` example
пристаням_стремятся_
.
<p>пристаням_стремятся_</p>
````````````````````````````````


Here `_` does not generate emphasis, because the first delimiter run
is right-flanking and the second left-flanking:

```````````````````````````````` example
aa_"bb"_cc
.
<p>aa_&quot;bb&quot;_cc</p>
````````````````````````````````


This is emphasis, even though the opening delimiter is
both left- and right-flanking, because it is preceded by
punctuation:

```````````````````````````````` example
foo-_(bar)_
.
<p>foo-<em>(bar)</em></p>
````````````````````````````````


Rule 3:

This is not emphasis, because the closing delimiter does
not match the opening delimiter:

```````````````````````````````` example
_foo*
.
<p>_foo*</p>
````````````````````````````````


This is not emphasis, because the closing `*` is preceded by
whitespace:

```````````````````````````````` example
*foo bar *
.
<p>*foo bar *</p>
````````````````````````````````


A newline also counts as whitespace:

```````````````````````````````` example
*foo bar
*
.
<p>*foo bar
*</p>
````````````````````````````````


This is not emphasis, because the second `*` is
preceded by punctuation and followed by an alphanumeric
(hence it is not part of a [right-flanking delimiter run]:

```````````````````````````````` example
*(*foo)
.
<p>*(*foo)</p>
````````````````````````````````


The point of this restriction is more easily appreciated
with this example:

```````````````````````````````` example
*(*foo*)*
.
<p><em>(<em>foo</em>)</em></p>
````````````````````````````````


Intraword emphasis with `*` is allowed:

```````````````````````````````` example
*foo*bar
.
<p><em>foo</em>bar</p>
````````````````````````````````



Rule 4:

This is not emphasis, because the closing `_` is preceded by
whitespace:

```````````````````````````````` example
_foo bar _
.
<p>_foo bar _</p>
````````````````````````````````


This is not emphasis, because the second `_` is
preceded by punctuation and followed by an alphanumeric:

```````````````````````````````` example
_(_foo)
.
<p>_(_foo)</p>
````````````````````````````````


This is emphasis within emphasis:

```````````````````````````````` example
_(_foo_)_
.
<p><em>(<em>foo</em>)</em></p>
````````````````````````````````


Intraword emphasis is disallowed for `_`:

```````````````````````````````` example
_foo_bar
.
<p>_foo_bar</p>
````````````````````````````````


```````````````````````````````` example
_пристаням_стремятся
.
<p>_пристаням_стремятся</p>
````````````````````````````````


```````````````````````````````` example
_foo_bar_baz_
.
<p><em>foo_bar_baz</em></p>
````````````````````````````````


This is emphasis, even though the closing delimiter is
both left- and right-flanking, because it is followed by
punctuation:

```````````````````````````````` example
_(bar)_.
.
<p><em>(bar)</em>.</p>
````````````````````````````````


Rule 5:

```````````````````````````````` example
**foo bar**
.
<p><strong>foo bar</strong></p>
````````````````````````````````


This is not strong emphasis, because the opening delimiter is
followed by whitespace:

```````````````````````````````` example
** foo bar**
.
<p>** foo bar**</p>
````````````````````````````````


This is not strong emphasis, because the opening `**` is preceded
by an alphanumeric and followed by punctuation, and hence
not part of a [left-flanking delimiter run]:

```````````````````````````````` example
a**"foo"**
.
<p>a**&quot;foo&quot;**</p>
````````````````````````````````


Intraword strong emphasis with `**` is permitted:

```````````````````````````````` example
foo**bar**
.
<p>foo<strong>bar</strong></p>
````````````````````````````````


Rule 6:

```````````````````````````````` example
__foo bar__
.
<p><strong>foo bar</strong></p>
````````````````````````````````


This is not strong emphasis, because the opening delimiter is
followed by whitespace:

```````````````````````````````` example
__ foo bar__
.
<p>__ foo bar__</p>
````````````````````````````````


A newline counts as whitespace:
```````````````````````````````` example
__
foo bar__
.
<p>__
foo bar__</p>
````````````````````````````````


This is not strong emphasis, because the opening `__` is preceded
by an alphanumeric and followed by punctuation:

```````````````````````````````` example
a__"foo"__
.
<p>a__&quot;foo&quot;__</p>
````````````````````````````````


Intraword strong emphasis is forbidden with `__`:

```````````````````````````````` example
foo__bar__
.
<p>foo__bar__</p>
````````````````````````````````


```````````````````````````````` example
5__6__78
.
<p>5__6__78</p>
````````````````````````````````


```````````````````````````````` example
пристаням__стремятся__
.
<p>пристаням__стремятся__</p>
````````````````````````````````


```````````````````````````````` example
__foo, __bar__, baz__
.
<p><strong>foo, bar, baz</strong></p>
````````````````````````````````


This is strong emphasis, even though the opening delimiter is
both left- and right-flanking, because it is preceded by
punctuation:

```````````````````````````````` example
foo-__(bar)__
.
<p>foo-<strong>(bar)</strong></p>
````````````````````````````````



Rule 7:

This is not strong emphasis, because the closing delimiter is preceded
by whitespace:

```````````````````````````````` example
**foo bar **
.
<p>**foo bar **</p>
````````````````````````````````


(Nor can it be interpreted as an emphasized `*foo bar *`, because of
Rule 11.)

This is not strong emphasis, because the second `**` is
preceded by punctuation and followed by an alphanumeric:

```````````````````````````````` example
**(**foo)
.
<p>**(**foo)</p>
````````````````````````````````


The point of this restriction is more easily appreciated
with these examples:

```````````````````````````````` example
*(**foo**)*
.
<p><em>(<strong>foo</strong>)</em></p>
````````````````````````````````


```````````````````````````````` example
**Gomphocarpus (*Gomphocarpus physocarpus*, syn.
*Asclepias physocarpa*)**
.
<p><strong>Gomphocarpus (<em>Gomphocarpus physocarpus</em>, syn.
<em>Asclepias physocarpa</em>)</strong></p>
````````````````````````````````


```````````````````````````````` example
**foo "*bar*" foo**
.
<p><strong>foo &quot;<em>bar</em>&quot; foo</strong></p>
````````````````````````````````


Intraword emphasis:

```````````````````````````````` example
**foo**bar
.
<p><strong>foo</strong>bar</p>
````````````````````````````````


Rule 8:

This is not strong emphasis, because the closing delimiter is
preceded by whitespace:

```````````````````````````````` example
__foo bar __
.
<p>__foo bar __</p>
````````````````````````````````


This is not strong emphasis, because the second `__` is
preceded by punctuation and followed by an alphanumeric:

```````````````````````````````` example
__(__foo)
.
<p>__(__foo)</p>
````````````````````````````````


The point of this restriction is more easily appreciated
with this example:

```````````````````````````````` example
_(__foo__)_
.
<p><em>(<strong>foo</strong>)</em></p>
````````````````````````````````


Intraword strong emphasis is forbidden with `__`:

```````````````````````````````` example
__foo__bar
.
<p>__foo__bar</p>
````````````````````````````````


```````````````````````````````` example
__пристаням__стремятся
.
<p>__пристаням__стремятся</p>
````````````````````````````````


```````````````````````````````` example
__foo__bar__baz__
.
<p><strong>foo__bar__baz</strong></p>
````````````````````````````````


This is strong emphasis, even though the closing delimiter is
both left- and right-flanking, because it is followed by
punctuation:

```````````````````````````````` example
__(bar)__.
.
<p><strong>(bar)</strong>.</p>
````````````````````````````````


Rule 9:

Any nonempty sequence of inline elements can be the contents of an
emphasized span.

```````````````````````````````` example
*foo [bar](/url)*
.
<p><em>foo <a href="/url">bar</a></em></p>
````````````````````````````````


```````````````````````````````` example
*foo
bar*
.
<p><em>foo
bar</em></p>
````````````````````````````````


In particular, emphasis and strong emphasis can be nested
inside emphasis:

```````````````````````````````` example
_foo __bar__ baz_
.
<p><em>foo <strong>bar</strong> baz</em></p>
````````````````````````````````


```````````````````````````````` example
_foo _bar_ baz_
.
<p><em>foo <em>bar</em> baz</em></p>
````````````````````````````````


```````````````````````````````` example
__foo_ bar_
.
<p><em><em>foo</em> bar</em></p>
````````````````````````````````


```````````````````````````````` example
*foo *bar**
.
<p><em>foo <em>bar</em></em></p>
````````````````````````````````


```````````````````````````````` example
*foo **bar** baz*
.
<p><em>foo <strong>bar</strong> baz</em></p>
````````````````````````````````

```````````````````````````````` example
*foo**bar**baz*
.
<p><em>foo<strong>bar</strong>baz</em></p>
````````````````````````````````

Note that in the preceding case, the interpretation

``` markdown
<p><em>foo</em><em>bar<em></em>baz</em></p>
```


is precluded by the condition that a delimiter that
can both open and close (like the `*` after `foo`)
cannot form emphasis if the sum of the lengths of
the delimiter runs containing the opening and
closing delimiters is a multiple of 3 unless
both lengths are multiples of 3.


For the same reason, we don't get two consecutive
emphasis sections in this example:

```````````````````````````````` example
*foo**bar*
.
<p><em>foo**bar</em></p>
````````````````````````````````


The same condition ensures that the following
cases are all strong emphasis nested inside
emphasis, even when the interior spaces are
omitted:


```````````````````````````````` example
***foo** bar*
.
<p><em><strong>foo</strong> bar</em></p>
````````````````````````````````


```````````````````````````````` example
*foo **bar***
.
<p><em>foo <strong>bar</strong></em></p>
````````````````````````````````


```````````````````````````````` example
*foo**bar***
.
<p><em>foo<strong>bar</strong></em></p>
````````````````````````````````


When the lengths of the interior closing and opening
delimiter runs are *both* multiples of 3, though,
they can match to create emphasis:

```````````````````````````````` example
foo***bar***baz
.
<p>foo<em><strong>bar</strong></em>baz</p>
````````````````````````````````

```````````````````````````````` example
foo******bar*********baz
.
<p>foo<strong>bar</strong>***baz</p>
````````````````````````````````


Indefinite levels of nesting are possible:

```````````````````````````````` example
*foo **bar *baz* bim** bop*
.
<p><em>foo <strong>bar <em>baz</em> bim</strong> bop</em></p>
````````````````````````````````


```````````````````````````````` example
*foo [*bar*](/url)*
.
<p><em>foo <a href="/url"><em>bar</em></a></em></p>
````````````````````````````````


There can be no empty emphasis or strong emphasis:

```````````````````````````````` example
** is not an empty emphasis
.
<p>** is not an empty emphasis</p>
````````````````````````````````


```````````````````````````````` example
**** is not an empty strong emphasis
.
<p>**** is not an empty strong emphasis</p>
````````````````````````````````



Rule 10:

Any nonempty sequence of inline elements can be the contents of an
strongly emphasized span.

```````````````````````````````` example
**foo [bar](/url)**
.
<p><strong>foo <a href="/url">bar</a></strong></p>
````````````````````````````````


```````````````````````````````` example
**foo
bar**
.
<p><strong>foo
bar</strong></p>
````````````````````````````````


In particular, emphasis and strong emphasis can be nested
inside strong emphasis:

```````````````````````````````` example
__foo _bar_ baz__
.
<p><strong>foo <em>bar</em> baz</strong></p>
````````````````````````````````


```````````````````````````````` example
__foo __bar__ baz__
.
<p><strong>foo bar baz</strong></p>
````````````````````````````````


```````````````````````````````` example
____foo__ bar__
.
<p><strong>foo bar</strong></p>
````````````````````````````````


```````````````````````````````` example
**foo **bar****
.
<p><strong>foo bar</strong></p>
````````````````````````````````


```````````````````````````````` example
**foo *bar* baz**
.
<p><strong>foo <em>bar</em> baz</strong></p>
````````````````````````````````


```````````````````````````````` example
**foo*bar*baz**
.
<p><strong>foo<em>bar</em>baz</strong></p>
````````````````````````````````


```````````````````````````````` example
***foo* bar**
.
<p><strong><em>foo</em> bar</strong></p>
````````````````````````````````


```````````````````````````````` example
**foo *bar***
.
<p><strong>foo <em>bar</em></strong></p>
````````````````````````````````


Indefinite levels of nesting are possible:

```````````````````````````````` example
**foo *bar **baz**
bim* bop**
.
<p><strong>foo <em>bar <strong>baz</strong>
bim</em> bop</strong></p>
````````````````````````````````


```````````````````````````````` example
**foo [*bar*](/url)**
.
<p><strong>foo <a href="/url"><em>bar</em></a></strong></p>
````````````````````````````````


There can be no empty emphasis or strong emphasis:

```````````````````````````````` example
__ is not an empty emphasis
.
<p>__ is not an empty emphasis</p>
````````````````````````````````


```````````````````````````````` example
____ is not an empty strong emphasis
.
<p>____ is not an empty strong emphasis</p>
````````````````````````````````



Rule 11:

```````````````````````````````` example
foo ***
.
<p>foo ***</p>
````````````````````````````````


```````````````````````````````` example
foo *\**
.
<p>foo <em>*</em></p>
````````````````````````````````


```````````````````````````````` example
foo *_*
.
<p>foo <em>_</em></p>
````````````````````````````````


```````````````````````````````` example
foo *****
.
<p>foo *****</p>
````````````````````````````````


```````````````````````````````` example
foo **\***
.
<p>foo <strong>*</strong></p>
````````````````````````````````


```````````````````````````````` example
foo **_**
.
<p>foo <strong>_</strong></p>
````````````````````````````````


Note that when delimiters do not match evenly, Rule 11 determines
that the excess literal `*` characters will appear outside of the
emphasis, rather than inside it:

```````````````````````````````` example
**foo*
.
<p>*<em>foo</em></p>
````````````````````````````````


```````````````````````````````` example
*foo**
.
<p><em>foo</em>*</p>
````````````````````````````````


```````````````````````````````` example
***foo**
.
<p>*<strong>foo</strong></p>
````````````````````````````````


```````````````````````````````` example
****foo*
.
<p>***<em>foo</em></p>
````````````````````````````````


```````````````````````````````` example
**foo***
.
<p><strong>foo</strong>*</p>
````````````````````````````````


```````````````````````````````` example
*foo****
.
<p><em>foo</em>***</p>
````````````````````````````````



Rule 12:

```````````````````````````````` example
foo ___
.
<p>foo ___</p>
````````````````````````````````


```````````````````````````````` example
foo _\__
.
<p>foo <em>_</em></p>
````````````````````````````````


```````````````````````````````` example
foo _*_
.
<p>foo <em>*</em></p>
````````````````````````````````


```````````````````````````````` example
foo _____
.
<p>foo _____</p>
````````````````````````````````


```````````````````````````````` example
foo __\___
.
<p>foo <strong>_</strong></p>
````````````````````````````````


```````````````````````````````` example
foo __*__
.
<p>foo <strong>*</strong></p>
````````````````````````````````


```````````````````````````````` example
__foo_
.
<p>_<em>foo</em></p>
````````````````````````````````


Note that when delimiters do not match evenly, Rule 12 determines
that the excess literal `_` characters will appear outside of the
emphasis, rather than inside it:

```````````````````````````````` example
_foo__
.
<p><em>foo</em>_</p>
````````````````````````````````


```````````````````````````````` example
___foo__
.
<p>_<strong>foo</strong></p>
````````````````````````````````


```````````````````````````````` example
____foo_
.
<p>___<em>foo</em></p>
````````````````````````````````


```````````````````````````````` example
__foo___
.
<p><strong>foo</strong>_</p>
````````````````````````````````


```````````````````````````````` example
_foo____
.
<p><em>foo</em>___</p>
````````````````````````````````


Rule 13 implies that if you want emphasis nested directly inside
emphasis, you must use different delimiters:

```````````````````````````````` example
**foo**
.
<p><strong>foo</strong></p>
````````````````````````````````


```````````````````````````````` example
*_foo_*
.
<p><em><em>foo</em></em></p>
````````````````````````````````


```````````````````````````````` example
__foo__
.
<p><strong>foo</strong></p>
````````````````````````````````


```````````````````````````````` example
_*foo*_
.
<p><em><em>foo</em></em></p>
````````````````````````````````


However, strong emphasis within strong emphasis is possible without
switching delimiters:

```````````````````````````````` example
****foo****
.
<p><strong>foo</strong></p>
````````````````````````````````


```````````````````````````````` example
____foo____
.
<p><strong>foo</strong></p>
````````````````````````````````



Rule 13 can be applied to arbitrarily long sequences of
delimiters:

```````````````````````````````` example
******foo******
.
<p><strong>foo</strong></p>
````````````````````````````````


Rule 14:

```````````````````````````````` example
***foo***
.
<p><em><strong>foo</strong></em></p>
````````````````````````````````


```````````````````````````````` example
_____foo_____
.
<p><em><strong>foo</strong></em></p>
````````````````````````````````


Rule 15:

```````````````````````````````` example
*foo _bar* baz_
.
<p><em>foo _bar</em> baz_</p>
````````````````````````````````


```````````````````````````````` example
*foo __bar *baz bim__ bam*
.
<p><em>foo <strong>bar *baz bim</strong> bam</em></p>
````````````````````````````````


Rule 16:

```````````````````````````````` example
**foo **bar baz**
.
<p>**foo <strong>bar baz</strong></p>
````````````````````````````````


```````````````````````````````` example
*foo *bar baz*
.
<p>*foo <em>bar baz</em></p>
````````````````````````````````


Rule 17:

```````````````````````````````` example
*[bar*](/url)
.
<p>*<a href="/url">bar*</a></p>
````````````````````````````````


```````````````````````````````` example
_foo [bar_](/url)
.
<p>_foo <a href="/url">bar_</a></p>
````````````````````````````````


```````````````````````````````` example
*<img src="foo" title="*"/>
.
<p>*<img src="foo" title="*"/></p>
````````````````````````````````


```````````````````````````````` example
**<a href="**">
.
<p>**<a href="**"></p>
````````````````````````````````


```````````````````````````````` example
__<a href="__">
.
<p>__<a href="__"></p>
````````````````````````````````


```````````````````````````````` example
*a `*`*
.
<p><em>a <code>*</code></em></p>
````````````````````````````````


```````````````````````````````` example
_a `_`_
.
<p><em>a <code>_</code></em></p>
````````````````````````````````


```````````````````````````````` example
**a<http://foo.bar/?q=**>
.
<p>**a<a href="http://foo.bar/?q=**">http://foo.bar/?q=**</a></p>
````````````````````````````````


```````````````````````````````` example
__a<http://foo.bar/?q=__>
.
<p>__a<a href="http://foo.bar/?q=__">http://foo.bar/?q=__</a></p>
````````````````````````````````


<div class="extension">

## Strikethrough (extension)

GFM enables the `strikethrough` extension, where an additional emphasis type is
available.

Strikethrough text is any text wrapped in two tildes (`~`).

```````````````````````````````` example strikethrough
~~Hi~~ Hello, world!
.
<p><del>Hi</del> Hello, world!</p>
````````````````````````````````

As with regular emphasis delimiters, a new paragraph will cause strikethrough
parsing to cease:

```````````````````````````````` example strikethrough
This ~~has a

new paragraph~~.
.
<p>This ~~has a</p>
<p>new paragraph~~.</p>
````````````````````````````````

</div>

## Links

A link contains [link text] (the visible text), a [link destination]
(the URI that is the link destination), and optionally a [link title].
There are two basic kinds of links in Markdown.  In [inline links] the
destination and title are given immediately after the link text.  In
[reference links] the destination and title are defined elsewhere in
the document.

A [link text](@) consists of a sequence of zero or more
inline elements enclosed by square brackets (`[` and `]`).  The
following rules apply:

- Links may not contain other links, at any level of nesting. If
  multiple otherwise valid link definitions appear nested inside each
  other, the inner-most definition is used.

- Brackets are allowed in the [link text] only if (a) they
  are backslash-escaped or (b) they appear as a matched pair of brackets,
  with an open bracket `[`, a sequence of zero or more inlines, and
  a close bracket `]`.

- Backtick [code spans], [autolinks], and raw [HTML tags] bind more tightly
  than the brackets in link text.  Thus, for example,
  `` [foo`]` `` could not be a link text, since the second `]`
  is part of a code span.

- The brackets in link text bind more tightly than markers for
  [emphasis and strong emphasis]. Thus, for example, `*[foo*](url)` is a link.

A [link destination](@) consists of either

- a sequence of zero or more characters between an opening `<` and a
  closing `>` that contains no line breaks or unescaped
  `<` or `>` characters, or

- a nonempty sequence of characters that does not start with
  `<`, does not include ASCII space or control characters, and
  includes parentheses only if (a) they are backslash-escaped or
  (b) they are part of a balanced pair of unescaped parentheses.
  (Implementations may impose limits on parentheses nesting to
  avoid performance issues, but at least three levels of nesting
  should be supported.)

A [link title](@)  consists of either

- a sequence of zero or more characters between straight double-quote
  characters (`"`), including a `"` character only if it is
  backslash-escaped, or

- a sequence of zero or more characters between straight single-quote
  characters (`'`), including a `'` character only if it is
  backslash-escaped, or

- a sequence of zero or more characters between matching parentheses
  (`(...)`), including a `(` or `)` character only if it is
  backslash-escaped.

Although [link titles] may span multiple lines, they may not contain
a [blank line].

An [inline link](@) consists of a [link text] followed immediately
by a left parenthesis `(`, optional [whitespace], an optional
[link destination], an optional [link title] separated from the link
destination by [whitespace], optional [whitespace], and a right
parenthesis `)`. The link's text consists of the inlines contained
in the [link text] (excluding the enclosing square brackets).
The link's URI consists of the link destination, excluding enclosing
`<...>` if present, with backslash-escapes in effect as described
above.  The link's title consists of the link title, excluding its
enclosing delimiters, with backslash-escapes in effect as described
above.

Here is a simple inline link:

```````````````````````````````` example
[link](/uri "title")
.
<p><a href="/uri" title="title">link</a></p>
````````````````````````````````


The title may be omitted:

```````````````````````````````` example
[link](/uri)
.
<p><a href="/uri">link</a></p>
````````````````````````````````


Both the title and the destination may be omitted:

```````````````````````````````` example
[link]()
.
<p><a href="">link</a></p>
````````````````````````````````


```````````````````````````````` example
[link](<>)
.
<p><a href="">link</a></p>
````````````````````````````````

The destination can only contain spaces if it is
enclosed in pointy brackets:

```````````````````````````````` example
[link](/my uri)
.
<p>[link](/my uri)</p>
````````````````````````````````

```````````````````````````````` example
[link](</my uri>)
.
<p><a href="/my%20uri">link</a></p>
````````````````````````````````

The destination cannot contain line breaks,
even if enclosed in pointy brackets:

```````````````````````````````` example
[link](foo
bar)
.
<p>[link](foo
bar)</p>
````````````````````````````````

```````````````````````````````` example
[link](<foo
bar>)
.
<p>[link](<foo
bar>)</p>
````````````````````````````````

The destination can contain `)` if it is enclosed
in pointy brackets:

```````````````````````````````` example
[a](<b)c>)
.
<p><a href="b)c">a</a></p>
````````````````````````````````

Pointy brackets that enclose links must be unescaped:

```````````````````````````````` example
[link](<foo\>)
.
<p>[link](&lt;foo&gt;)</p>
````````````````````````````````

These are not links, because the opening pointy bracket
is not matched properly:

```````````````````````````````` example
[a](<b)c
[a](<b)c>
[a](<b>c)
.
<p>[a](&lt;b)c
[a](&lt;b)c&gt;
[a](<b>c)</p>
````````````````````````````````

Parentheses inside the link destination may be escaped:

```````````````````````````````` example
[link](\(foo\))
.
<p><a href="(foo)">link</a></p>
````````````````````````````````

Any number of parentheses are allowed without escaping, as long as they are
balanced:

```````````````````````````````` example
[link](foo(and(bar)))
.
<p><a href="foo(and(bar))">link</a></p>
````````````````````````````````

However, if you have unbalanced parentheses, you need to escape or use the
`<...>` form:

```````````````````````````````` example
[link](foo\(and\(bar\))
.
<p><a href="foo(and(bar)">link</a></p>
````````````````````````````````


```````````````````````````````` example
[link](<foo(and(bar)>)
.
<p><a href="foo(and(bar)">link</a></p>
````````````````````````````````


Parentheses and other symbols can also be escaped, as usual
in Markdown:

```````````````````````````````` example
[link](foo\)\:)
.
<p><a href="foo):">link</a></p>
````````````````````````````````


A link can contain fragment identifiers and queries:

```````````````````````````````` example
[link](#fragment)

[link](http://example.com#fragment)

[link](http://example.com?foo=3#frag)
.
<p><a href="#fragment">link</a></p>
<p><a href="http://example.com#fragment">link</a></p>
<p><a href="http://example.com?foo=3#frag">link</a></p>
````````````````````````````````


Note that a backslash before a non-escapable character is
just a backslash:

```````````````````````````````` example
[link](foo\bar)
.
<p><a href="foo%5Cbar">link</a></p>
````````````````````````````````


URL-escaping should be left alone inside the destination, as all
URL-escaped characters are also valid URL characters. Entity and
numerical character references in the destination will be parsed
into the corresponding Unicode code points, as usual.  These may
be optionally URL-escaped when written as HTML, but this spec
does not enforce any particular policy for rendering URLs in
HTML or other formats.  Renderers may make different decisions
about how to escape or normalize URLs in the output.

```````````````````````````````` example
[link](foo%20b&auml;)
.
<p><a href="foo%20b%C3%A4">link</a></p>
````````````````````````````````


Note that, because titles can often be parsed as destinations,
if you try to omit the destination and keep the title, you'll
get unexpected results:

```````````````````````````````` example
[link]("title")
.
<p><a href="%22title%22">link</a></p>
````````````````````````````````


Titles may be in single quotes, double quotes, or parentheses:

```````````````````````````````` example
[link](/url "title")
[link](/url 'title')
[link](/url (title))
.
<p><a href="/url" title="title">link</a>
<a href="/url" title="title">link</a>
<a href="/url" title="title">link</a></p>
````````````````````````````````


Backslash escapes and entity and numeric character references
may be used in titles:

```````````````````````````````` example
[link](/url "title \"&quot;")
.
<p><a href="/url" title="title &quot;&quot;">link</a></p>
````````````````````````````````


Titles must be separated from the link using a [whitespace].
Other [Unicode whitespace] like non-breaking space doesn't work.

```````````````````````````````` example
[link](/url "title")
.
<p><a href="/url%C2%A0%22title%22">link</a></p>
````````````````````````````````


Nested balanced quotes are not allowed without escaping:

```````````````````````````````` example
[link](/url "title "and" title")
.
<p>[link](/url &quot;title &quot;and&quot; title&quot;)</p>
````````````````````````````````


But it is easy to work around this by using a different quote type:

```````````````````````````````` example
[link](/url 'title "and" title')
.
<p><a href="/url" title="title &quot;and&quot; title">link</a></p>
````````````````````````````````


(Note:  `Markdown.pl` did allow double quotes inside a double-quoted
title, and its test suite included a test demonstrating this.
But it is hard to see a good rationale for the extra complexity this
brings, since there are already many ways---backslash escaping,
entity and numeric character references, or using a different
quote type for the enclosing title---to write titles containing
double quotes.  `Markdown.pl`'s handling of titles has a number
of other strange features.  For example, it allows single-quoted
titles in inline links, but not reference links.  And, in
reference links but not inline links, it allows a title to begin
with `"` and end with `)`.  `Markdown.pl` 1.0.1 even allows
titles with no closing quotation mark, though 1.0.2b8 does not.
It seems preferable to adopt a simple, rational rule that works
the same way in inline links and link reference definitions.)

[Whitespace] is allowed around the destination and title:

```````````````````````````````` example
[link](   /uri
  "title"  )
.
<p><a href="/uri" title="title">link</a></p>
````````````````````````````````


But it is not allowed between the link text and the
following parenthesis:

```````````````````````````````` example
[link] (/uri)
.
<p>[link] (/uri)</p>
````````````````````````````````


The link text may contain balanced brackets, but not unbalanced ones,
unless they are escaped:

```````````````````````````````` example
[link [foo [bar]]](/uri)
.
<p><a href="/uri">link [foo [bar]]</a></p>
````````````````````````````````


```````````````````````````````` example
[link] bar](/uri)
.
<p>[link] bar](/uri)</p>
````````````````````````````````


```````````````````````````````` example
[link [bar](/uri)
.
<p>[link <a href="/uri">bar</a></p>
````````````````````````````````


```````````````````````````````` example
[link \[bar](/uri)
.
<p><a href="/uri">link [bar</a></p>
````````````````````````````````


The link text may contain inline content:

```````````````````````````````` example
[link *foo **bar** `#`*](/uri)
.
<p><a href="/uri">link <em>foo <strong>bar</strong> <code>#</code></em></a></p>
````````````````````````````````


```````````````````````````````` example
[![moon](moon.jpg)](/uri)
.
<p><a href="/uri"><img src="moon.jpg" alt="moon" /></a></p>
````````````````````````````````


However, links may not contain other links, at any level of nesting.

```````````````````````````````` example
[foo [bar](/uri)](/uri)
.
<p>[foo <a href="/uri">bar</a>](/uri)</p>
````````````````````````````````


```````````````````````````````` example
[foo *[bar [baz](/uri)](/uri)*](/uri)
.
<p>[foo <em>[bar <a href="/uri">baz</a>](/uri)</em>](/uri)</p>
````````````````````````````````


```````````````````````````````` example
![[[foo](uri1)](uri2)](uri3)
.
<p><img src="uri3" alt="[foo](uri2)" /></p>
````````````````````````````````


These cases illustrate the precedence of link text grouping over
emphasis grouping:

```````````````````````````````` example
*[foo*](/uri)
.
<p>*<a href="/uri">foo*</a></p>
````````````````````````````````


```````````````````````````````` example
[foo *bar](baz*)
.
<p><a href="baz*">foo *bar</a></p>
````````````````````````````````


Note that brackets that *aren't* part of links do not take
precedence:

```````````````````````````````` example
*foo [bar* baz]
.
<p><em>foo [bar</em> baz]</p>
````````````````````````````````


These cases illustrate the precedence of HTML tags, code spans,
and autolinks over link grouping:

```````````````````````````````` example
[foo <bar attr="](baz)">
.
<p>[foo <bar attr="](baz)"></p>
````````````````````````````````


```````````````````````````````` example
[foo`](/uri)`
.
<p>[foo<code>](/uri)</code></p>
````````````````````````````````


```````````````````````````````` example
[foo<http://example.com/?search=](uri)>
.
<p>[foo<a href="http://example.com/?search=%5D(uri)">http://example.com/?search=](uri)</a></p>
````````````````````````````````


There are three kinds of [reference link](@)s:
[full](#full-reference-link), [collapsed](#collapsed-reference-link),
and [shortcut](#shortcut-reference-link).

A [full reference link](@)
consists of a [link text] immediately followed by a [link label]
that [matches] a [link reference definition] elsewhere in the document.

A [link label](@)  begins with a left bracket (`[`) and ends
with the first right bracket (`]`) that is not backslash-escaped.
Between these brackets there must be at least one [non-whitespace character].
Unescaped square bracket characters are not allowed inside the
opening and closing square brackets of [link labels].  A link
label can have at most 999 characters inside the square
brackets.

One label [matches](@)
another just in case their normalized forms are equal.  To normalize a
label, strip off the opening and closing brackets,
perform the *Unicode case fold*, strip leading and trailing
[whitespace] and collapse consecutive internal
[whitespace] to a single space.  If there are multiple
matching reference link definitions, the one that comes first in the
document is used.  (It is desirable in such cases to emit a warning.)

The contents of the first link label are parsed as inlines, which are
used as the link's text.  The link's URI and title are provided by the
matching [link reference definition].

Here is a simple example:

```````````````````````````````` example
[foo][bar]

[bar]: /url "title"
.
<p><a href="/url" title="title">foo</a></p>
````````````````````````````````


The rules for the [link text] are the same as with
[inline links].  Thus:

The link text may contain balanced brackets, but not unbalanced ones,
unless they are escaped:

```````````````````````````````` example
[link [foo [bar]]][ref]

[ref]: /uri
.
<p><a href="/uri">link [foo [bar]]</a></p>
````````````````````````````````


```````````````````````````````` example
[link \[bar][ref]

[ref]: /uri
.
<p><a href="/uri">link [bar</a></p>
````````````````````````````````


The link text may contain inline content:

```````````````````````````````` example
[link *foo **bar** `#`*][ref]

[ref]: /uri
.
<p><a href="/uri">link <em>foo <strong>bar</strong> <code>#</code></em></a></p>
````````````````````````````````


```````````````````````````````` example
[![moon](moon.jpg)][ref]

[ref]: /uri
.
<p><a href="/uri"><img src="moon.jpg" alt="moon" /></a></p>
````````````````````````````````


However, links may not contain other links, at any level of nesting.

```````````````````````````````` example
[foo [bar](/uri)][ref]

[ref]: /uri
.
<p>[foo <a href="/uri">bar</a>]<a href="/uri">ref</a></p>
````````````````````````````````


```````````````````````````````` example
[foo *bar [baz][ref]*][ref]

[ref]: /uri
.
<p>[foo <em>bar <a href="/uri">baz</a></em>]<a href="/uri">ref</a></p>
````````````````````````````````


(In the examples above, we have two [shortcut reference links]
instead of one [full reference link].)

The following cases illustrate the precedence of link text grouping over
emphasis grouping:

```````````````````````````````` example
*[foo*][ref]

[ref]: /uri
.
<p>*<a href="/uri">foo*</a></p>
````````````````````````````````


```````````````````````````````` example
[foo *bar][ref]

[ref]: /uri
.
<p><a href="/uri">foo *bar</a></p>
````````````````````````````````


These cases illustrate the precedence of HTML tags, code spans,
and autolinks over link grouping:

```````````````````````````````` example
[foo <bar attr="][ref]">

[ref]: /uri
.
<p>[foo <bar attr="][ref]"></p>
````````````````````````````````


```````````````````````````````` example
[foo`][ref]`

[ref]: /uri
.
<p>[foo<code>][ref]</code></p>
````````````````````````````````


```````````````````````````````` example
[foo<http://example.com/?search=][ref]>

[ref]: /uri
.
<p>[foo<a href="http://example.com/?search=%5D%5Bref%5D">http://example.com/?search=][ref]</a></p>
````````````````````````````````


Matching is case-insensitive:

```````````````````````````````` example
[foo][BaR]

[bar]: /url "title"
.
<p><a href="/url" title="title">foo</a></p>
````````````````````````````````


Unicode case fold is used:

```````````````````````````````` example
[Толпой][Толпой] is a Russian word.

[ТОЛПОЙ]: /url
.
<p><a href="/url">Толпой</a> is a Russian word.</p>
````````````````````````````````


Consecutive internal [whitespace] is treated as one space for
purposes of determining matching:

```````````````````````````````` example
[Foo
  bar]: /url

[Baz][Foo bar]
.
<p><a href="/url">Baz</a></p>
````````````````````````````````


No [whitespace] is allowed between the [link text] and the
[link label]:

```````````````````````````````` example
[foo] [bar]

[bar]: /url "title"
.
<p>[foo] <a href="/url" title="title">bar</a></p>
````````````````````````````````


```````````````````````````````` example
[foo]
[bar]

[bar]: /url "title"
.
<p>[foo]
<a href="/url" title="title">bar</a></p>
````````````````````````````````


This is a departure from John Gruber's original Markdown syntax
description, which explicitly allows whitespace between the link
text and the link label.  It brings reference links in line with
[inline links], which (according to both original Markdown and
this spec) cannot have whitespace after the link text.  More
importantly, it prevents inadvertent capture of consecutive
[shortcut reference links]. If whitespace is allowed between the
link text and the link label, then in the following we will have
a single reference link, not two shortcut reference links, as
intended:

``` markdown
[foo]
[bar]

[foo]: /url1
[bar]: /url2
```

(Note that [shortcut reference links] were introduced by Gruber
himself in a beta version of `Markdown.pl`, but never included
in the official syntax description.  Without shortcut reference
links, it is harmless to allow space between the link text and
link label; but once shortcut references are introduced, it is
too dangerous to allow this, as it frequently leads to
unintended results.)

When there are multiple matching [link reference definitions],
the first is used:

```````````````````````````````` example
[foo]: /url1

[foo]: /url2

[bar][foo]
.
<p><a href="/url1">bar</a></p>
````````````````````````````````


Note that matching is performed on normalized strings, not parsed
inline content.  So the following does not match, even though the
labels define equivalent inline content:

```````````````````````````````` example
[bar][foo\!]

[foo!]: /url
.
<p>[bar][foo!]</p>
````````````````````````````````


[Link labels] cannot contain brackets, unless they are
backslash-escaped:

```````````````````````````````` example
[foo][ref[]

[ref[]: /uri
.
<p>[foo][ref[]</p>
<p>[ref[]: /uri</p>
````````````````````````````````


```````````````````````````````` example
[foo][ref[bar]]

[ref[bar]]: /uri
.
<p>[foo][ref[bar]]</p>
<p>[ref[bar]]: /uri</p>
````````````````````````````````


```````````````````````````````` example
[[[foo]]]

[[[foo]]]: /url
.
<p>[[[foo]]]</p>
<p>[[[foo]]]: /url</p>
````````````````````````````````


```````````````````````````````` example
[foo][ref\[]

[ref\[]: /uri
.
<p><a href="/uri">foo</a></p>
````````````````````````````````


Note that in this example `]` is not backslash-escaped:

```````````````````````````````` example
[bar\\]: /uri

[bar\\]
.
<p><a href="/uri">bar\</a></p>
````````````````````````````````


A [link label] must contain at least one [non-whitespace character]:

```````````````````````````````` example
[]

[]: /uri
.
<p>[]</p>
<p>[]: /uri</p>
````````````````````````````````


```````````````````````````````` example
[
 ]

[
 ]: /uri
.
<p>[
]</p>
<p>[
]: /uri</p>
````````````````````````````````


A [collapsed reference link](@)
consists of a [link label] that [matches] a
[link reference definition] elsewhere in the
document, followed by the string `[]`.
The contents of the first link label are parsed as inlines,
which are used as the link's text.  The link's URI and title are
provided by the matching reference link definition.  Thus,
`[foo][]` is equivalent to `[foo][foo]`.

```````````````````````````````` example
[foo][]

[foo]: /url "title"
.
<p><a href="/url" title="title">foo</a></p>
````````````````````````````````


```````````````````````````````` example
[*foo* bar][]

[*foo* bar]: /url "title"
.
<p><a href="/url" title="title"><em>foo</em> bar</a></p>
````````````````````````````````


The link labels are case-insensitive:

```````````````````````````````` example
[Foo][]

[foo]: /url "title"
.
<p><a href="/url" title="title">Foo</a></p>
````````````````````````````````



As with full reference links, [whitespace] is not
allowed between the two sets of brackets:

```````````````````````````````` example
[foo]
[]

[foo]: /url "title"
.
<p><a href="/url" title="title">foo</a>
[]</p>
````````````````````````````````


A [shortcut reference link](@)
consists of a [link label] that [matches] a
[link reference definition] elsewhere in the
document and is not followed by `[]` or a link label.
The contents of the first link label are parsed as inlines,
which are used as the link's text.  The link's URI and title
are provided by the matching link reference definition.
Thus, `[foo]` is equivalent to `[foo][]`.

```````````````````````````````` example
[foo]

[foo]: /url "title"
.
<p><a href="/url" title="title">foo</a></p>
````````````````````````````````


```````````````````````````````` example
[*foo* bar]

[*foo* bar]: /url "title"
.
<p><a href="/url" title="title"><em>foo</em> bar</a></p>
````````````````````````````````


```````````````````````````````` example
[[*foo* bar]]

[*foo* bar]: /url "title"
.
<p>[<a href="/url" title="title"><em>foo</em> bar</a>]</p>
````````````````````````````````


```````````````````````````````` example
[[bar [foo]

[foo]: /url
.
<p>[[bar <a href="/url">foo</a></p>
````````````````````````````````


The link labels are case-insensitive:

```````````````````````````````` example
[Foo]

[foo]: /url "title"
.
<p><a href="/url" title="title">Foo</a></p>
````````````````````````````````


A space after the link text should be preserved:

```````````````````````````````` example
[foo] bar

[foo]: /url
.
<p><a href="/url">foo</a> bar</p>
````````````````````````````````


If you just want bracketed text, you can backslash-escape the
opening bracket to avoid links:

```````````````````````````````` example
\[foo]

[foo]: /url "title"
.
<p>[foo]</p>
````````````````````````````````


Note that this is a link, because a link label ends with the first
following closing bracket:

```````````````````````````````` example
[foo*]: /url

*[foo*]
.
<p>*<a href="/url">foo*</a></p>
````````````````````````````````


Full and compact references take precedence over shortcut
references:

```````````````````````````````` example
[foo][bar]

[foo]: /url1
[bar]: /url2
.
<p><a href="/url2">foo</a></p>
````````````````````````````````

```````````````````````````````` example
[foo][]

[foo]: /url1
.
<p><a href="/url1">foo</a></p>
````````````````````````````````

Inline links also take precedence:

```````````````````````````````` example
[foo]()

[foo]: /url1
.
<p><a href="">foo</a></p>
````````````````````````````````

```````````````````````````````` example
[foo](not a link)

[foo]: /url1
.
<p><a href="/url1">foo</a>(not a link)</p>
````````````````````````````````

In the following case `[bar][baz]` is parsed as a reference,
`[foo]` as normal text:

```````````````````````````````` example
[foo][bar][baz]

[baz]: /url
.
<p>[foo]<a href="/url">bar</a></p>
````````````````````````````````


Here, though, `[foo][bar]` is parsed as a reference, since
`[bar]` is defined:

```````````````````````````````` example
[foo][bar][baz]

[baz]: /url1
[bar]: /url2
.
<p><a href="/url2">foo</a><a href="/url1">baz</a></p>
````````````````````````````````


Here `[foo]` is not parsed as a shortcut reference, because it
is followed by a link label (even though `[bar]` is not defined):

```````````````````````````````` example
[foo][bar][baz]

[baz]: /url1
[foo]: /url2
.
<p>[foo]<a href="/url1">bar</a></p>
````````````````````````````````



## Images

Syntax for images is like the syntax for links, with one
difference. Instead of [link text], we have an
[image description](@).  The rules for this are the
same as for [link text], except that (a) an
image description starts with `![` rather than `[`, and
(b) an image description may contain links.
An image description has inline elements
as its contents.  When an image is rendered to HTML,
this is standardly used as the image's `alt` attribute.

```````````````````````````````` example
![foo](/url "title")
.
<p><img src="/url" alt="foo" title="title" /></p>
````````````````````````````````


```````````````````````````````` example
![foo *bar*]

[foo *bar*]: train.jpg "train & tracks"
.
<p><img src="train.jpg" alt="foo bar" title="train &amp; tracks" /></p>
````````````````````````````````


```````````````````````````````` example
![foo ![bar](/url)](/url2)
.
<p><img src="/url2" alt="foo bar" /></p>
````````````````````````````````


```````````````````````````````` example
![foo [bar](/url)](/url2)
.
<p><img src="/url2" alt="foo bar" /></p>
````````````````````````````````


Though this spec is concerned with parsing, not rendering, it is
recommended that in rendering to HTML, only the plain string content
of the [image description] be used.  Note that in
the above example, the alt attribute's value is `foo bar`, not `foo
[bar](/url)` or `foo <a href="/url">bar</a>`.  Only the plain string
content is rendered, without formatting.

```````````````````````````````` example
![foo *bar*][]

[foo *bar*]: train.jpg "train & tracks"
.
<p><img src="train.jpg" alt="foo bar" title="train &amp; tracks" /></p>
````````````````````````````````


```````````````````````````````` example
![foo *bar*][foobar]

[FOOBAR]: train.jpg "train & tracks"
.
<p><img src="train.jpg" alt="foo bar" title="train &amp; tracks" /></p>
````````````````````````````````


```````````````````````````````` example
![foo](train.jpg)
.
<p><img src="train.jpg" alt="foo" /></p>
````````````````````````````````


```````````````````````````````` example
My ![foo bar](/path/to/train.jpg  "title"   )
.
<p>My <img src="/path/to/train.jpg" alt="foo bar" title="title" /></p>
````````````````````````````````


```````````````````````````````` example
![foo](<url>)
.
<p><img src="url" alt="foo" /></p>
````````````````````````````````


```````````````````````````````` example
![](/url)
.
<p><img src="/url" alt="" /></p>
````````````````````````````````


Reference-style:

```````````````````````````````` example
![foo][bar]

[bar]: /url
.
<p><img src="/url" alt="foo" /></p>
````````````````````````````````


```````````````````````````````` example
![foo][bar]

[BAR]: /url
.
<p><img src="/url" alt="foo" /></p>
````````````````````````````````


Collapsed:

```````````````````````````````` example
![foo][]

[foo]: /url "title"
.
<p><img src="/url" alt="foo" title="title" /></p>
````````````````````````````````


```````````````````````````````` example
![*foo* bar][]

[*foo* bar]: /url "title"
.
<p><img src="/url" alt="foo bar" title="title" /></p>
````````````````````````````````


The labels are case-insensitive:

```````````````````````````````` example
![Foo][]

[foo]: /url "title"
.
<p><img src="/url" alt="Foo" title="title" /></p>
````````````````````````````````


As with reference links, [whitespace] is not allowed
between the two sets of brackets:

```````````````````````````````` example
![foo]
[]

[foo]: /url "title"
.
<p><img src="/url" alt="foo" title="title" />
[]</p>
````````````````````````````````


Shortcut:

```````````````````````````````` example
![foo]

[foo]: /url "title"
.
<p><img src="/url" alt="foo" title="title" /></p>
````````````````````````````````


```````````````````````````````` example
![*foo* bar]

[*foo* bar]: /url "title"
.
<p><img src="/url" alt="foo bar" title="title" /></p>
````````````````````````````````


Note that link labels cannot contain unescaped brackets:

```````````````````````````````` example
![[foo]]

[[foo]]: /url "title"
.
<p>![[foo]]</p>
<p>[[foo]]: /url &quot;title&quot;</p>
````````````````````````````````


The link labels are case-insensitive:

```````````````````````````````` example
![Foo]

[foo]: /url "title"
.
<p><img src="/url" alt="Foo" title="title" /></p>
````````````````````````````````


If you just want a literal `!` followed by bracketed text, you can
backslash-escape the opening `[`:

```````````````````````````````` example
!\[foo]

[foo]: /url "title"
.
<p>![foo]</p>
````````````````````````````````


If you want a link after a literal `!`, backslash-escape the
`!`:

```````````````````````````````` example
\![foo]

[foo]: /url "title"
.
<p>!<a href="/url" title="title">foo</a></p>
````````````````````````````````


## Autolinks

[Autolink](@)s are absolute URIs and email addresses inside
`<` and `>`. They are parsed as links, with the URL or email address
as the link label.

A [URI autolink](@) consists of `<`, followed by an
[absolute URI] followed by `>`.  It is parsed as
a link to the URI, with the URI as the link's label.

An [absolute URI](@),
for these purposes, consists of a [scheme] followed by a colon (`:`)
followed by zero or more characters other than ASCII
[whitespace] and control characters, `<`, and `>`.  If
the URI includes these characters, they must be percent-encoded
(e.g. `%20` for a space).

For purposes of this spec, a [scheme](@) is any sequence
of 2--32 characters beginning with an ASCII letter and followed
by any combination of ASCII letters, digits, or the symbols plus
("+"), period ("."), or hyphen ("-").

Here are some valid autolinks:

```````````````````````````````` example
<http://foo.bar.baz>
.
<p><a href="http://foo.bar.baz">http://foo.bar.baz</a></p>
````````````````````````````````


```````````````````````````````` example
<http://foo.bar.baz/test?q=hello&id=22&boolean>
.
<p><a href="http://foo.bar.baz/test?q=hello&amp;id=22&amp;boolean">http://foo.bar.baz/test?q=hello&amp;id=22&amp;boolean</a></p>
````````````````````````````````


```````````````````````````````` example
<irc://foo.bar:2233/baz>
.
<p><a href="irc://foo.bar:2233/baz">irc://foo.bar:2233/baz</a></p>
````````````````````````````````


Uppercase is also fine:

```````````````````````````````` example
<MAILTO:FOO@BAR.BAZ>
.
<p><a href="MAILTO:FOO@BAR.BAZ">MAILTO:FOO@BAR.BAZ</a></p>
````````````````````````````````


Note that many strings that count as [absolute URIs] for
purposes of this spec are not valid URIs, because their
schemes are not registered or because of other problems
with their syntax:

```````````````````````````````` example
<a+b+c:d>
.
<p><a href="a+b+c:d">a+b+c:d</a></p>
````````````````````````````````


```````````````````````````````` example
<made-up-scheme://foo,bar>
.
<p><a href="made-up-scheme://foo,bar">made-up-scheme://foo,bar</a></p>
````````````````````````````````


```````````````````````````````` example
<http://../>
.
<p><a href="http://../">http://../</a></p>
````````````````````````````````


```````````````````````````````` example
<localhost:5001/foo>
.
<p><a href="localhost:5001/foo">localhost:5001/foo</a></p>
````````````````````````````````


Spaces are not allowed in autolinks:

```````````````````````````````` example
<http://foo.bar/baz bim>
.
<p>&lt;http://foo.bar/baz bim&gt;</p>
````````````````````````````````


Backslash-escapes do not work inside autolinks:

```````````````````````````````` example
<http://example.com/\[\>
.
<p><a href="http://example.com/%5C%5B%5C">http://example.com/\[\</a></p>
````````````````````````````````


An [email autolink](@)
consists of `<`, followed by an [email address],
followed by `>`.  The link's label is the email address,
and the URL is `mailto:` followed by the email address.

An [email address](@),
for these purposes, is anything that matches
the [non-normative regex from the HTML5
spec](https://html.spec.whatwg.org/multipage/forms.html#e-mail-state-(type=email)):

    /^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?
    (?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$/

Examples of email autolinks:

```````````````````````````````` example
<foo@bar.example.com>
.
<p><a href="mailto:foo@bar.example.com">foo@bar.example.com</a></p>
````````````````````````````````


```````````````````````````````` example
<foo+special@Bar.baz-bar0.com>
.
<p><a href="mailto:foo+special@Bar.baz-bar0.com">foo+special@Bar.baz-bar0.com</a></p>
````````````````````````````````


Backslash-escapes do not work inside email autolinks:

```````````````````````````````` example
<foo\+@bar.example.com>
.
<p>&lt;foo+@bar.example.com&gt;</p>
````````````````````````````````


These are not autolinks:

```````````````````````````````` example
<>
.
<p>&lt;&gt;</p>
````````````````````````````````


```````````````````````````````` example
< http://foo.bar >
.
<p>&lt; http://foo.bar &gt;</p>
````````````````````````````````


```````````````````````````````` example
<m:abc>
.
<p>&lt;m:abc&gt;</p>
````````````````````````````````


```````````````````````````````` example
<foo.bar.baz>
.
<p>&lt;foo.bar.baz&gt;</p>
````````````````````````````````


```````````````````````````````` example
http://example.com
.
<p>http://example.com</p>
````````````````````````````````


```````````````````````````````` example
foo@bar.example.com
.
<p>foo@bar.example.com</p>
````````````````````````````````

<div class="extension">

## Autolinks (extension)

GFM enables the `autolink` extension, where autolinks will be recognised in a
greater number of conditions.

[Autolink]s can also be constructed without requiring the use of `<` and to `>`
to delimit them, although they will be recognized under a smaller set of
circumstances.  All such recognized autolinks can only come at the beginning of
a line, after whitespace, or any of the delimiting characters `*`, `_`, `~`,
and `(`.

An [extended www autolink](@) will be recognized
when the text `www.` is found followed by a [valid domain].
A [valid domain](@) consists of segments
of alphanumeric characters, underscores (`_`) and hyphens (`-`)
separated by periods (`.`).
There must be at least one period,
and no underscores may be present in the last two segments of the domain.

The scheme `http` will be inserted automatically:

```````````````````````````````` example autolink
www.commonmark.org
.
<p><a href="http://www.commonmark.org">www.commonmark.org</a></p>
````````````````````````````````

After a [valid domain], zero or more non-space non-`<` characters may follow:

```````````````````````````````` example autolink
Visit www.commonmark.org/help for more information.
.
<p>Visit <a href="http://www.commonmark.org/help">www.commonmark.org/help</a> for more information.</p>
````````````````````````````````

We then apply [extended autolink path validation](@) as follows:

Trailing punctuation (specifically, `?`, `!`, `.`, `,`, `:`, `*`, `_`, and `~`)
will not be considered part of the autolink, though they may be included in the
interior of the link:

```````````````````````````````` example autolink
Visit www.commonmark.org.

Visit www.commonmark.org/a.b.
.
<p>Visit <a href="http://www.commonmark.org">www.commonmark.org</a>.</p>
<p>Visit <a href="http://www.commonmark.org/a.b">www.commonmark.org/a.b</a>.</p>
````````````````````````````````

When an autolink ends in `)`, we scan the entire autolink for the total number
of parentheses.  If there is a greater number of closing parentheses than
opening ones, we don't consider the unmatched trailing parentheses part of the
autolink, in order to facilitate including an autolink inside a parenthesis:

```````````````````````````````` example autolink
www.google.com/search?q=Markup+(business)

www.google.com/search?q=Markup+(business)))

(www.google.com/search?q=Markup+(business))

(www.google.com/search?q=Markup+(business)
.
<p><a href="http://www.google.com/search?q=Markup+(business)">www.google.com/search?q=Markup+(business)</a></p>
<p><a href="http://www.google.com/search?q=Markup+(business)">www.google.com/search?q=Markup+(business)</a>))</p>
<p>(<a href="http://www.google.com/search?q=Markup+(business)">www.google.com/search?q=Markup+(business)</a>)</p>
<p>(<a href="http://www.google.com/search?q=Markup+(business)">www.google.com/search?q=Markup+(business)</a></p>
````````````````````````````````

This check is only done when the link ends in a closing parentheses `)`, so if
the only parentheses are in the interior of the autolink, no special rules are
applied:

```````````````````````````````` example autolink
www.google.com/search?q=(business))+ok
.
<p><a href="http://www.google.com/search?q=(business))+ok">www.google.com/search?q=(business))+ok</a></p>
````````````````````````````````

If an autolink ends in a semicolon (`;`), we check to see if it appears to
resemble an [entity reference][entity references]; if the preceding text is `&`
followed by one or more alphanumeric characters.  If so, it is excluded from
the autolink:

```````````````````````````````` example autolink
www.google.com/search?q=commonmark&hl=en

www.google.com/search?q=commonmark&hl;
.
<p><a href="http://www.google.com/search?q=commonmark&amp;hl=en">www.google.com/search?q=commonmark&amp;hl=en</a></p>
<p><a href="http://www.google.com/search?q=commonmark">www.google.com/search?q=commonmark</a>&amp;hl;</p>
````````````````````````````````

`<` immediately ends an autolink.

```````````````````````````````` example autolink
www.commonmark.org/he<lp
.
<p><a href="http://www.commonmark.org/he">www.commonmark.org/he</a>&lt;lp</p>
````````````````````````````````

An [extended url autolink](@) will be recognised when one of the schemes
`http://`, `https://`, or `ftp://`, followed by a [valid domain], then zero or
more non-space non-`<` characters according to
[extended autolink path validation]:

```````````````````````````````` example autolink
http://commonmark.org

(Visit https://encrypted.google.com/search?q=Markup+(business))

Anonymous FTP is available at ftp://foo.bar.baz.
.
<p><a href="http://commonmark.org">http://commonmark.org</a></p>
<p>(Visit <a href="https://encrypted.google.com/search?q=Markup+(business)">https://encrypted.google.com/search?q=Markup+(business)</a>)</p>
<p>Anonymous FTP is available at <a href="ftp://foo.bar.baz">ftp://foo.bar.baz</a>.</p>
````````````````````````````````


An [extended email autolink](@) will be recognised when an email address is
recognised within any text node.  Email addresses are recognised according to
the following rules:

* One ore more characters which are alphanumeric, or `.`, `-`, `_`, or `+`.
* An `@` symbol.
* One or more characters which are alphanumeric, or `-` or `_`,
  separated by periods (`.`).
  There must be at least one period.
  The last character must not be one of `-` or `_`.

The scheme `mailto:` will automatically be added to the generated link:

```````````````````````````````` example autolink
foo@bar.baz
.
<p><a href="mailto:foo@bar.baz">foo@bar.baz</a></p>
````````````````````````````````

`+` can occur before the `@`, but not after.

```````````````````````````````` example autolink
hello@mail+xyz.example isn't valid, but hello+xyz@mail.example is.
.
<p>hello@mail+xyz.example isn't valid, but <a href="mailto:hello+xyz@mail.example">hello+xyz@mail.example</a> is.</p>
````````````````````````````````

`.`, `-`, and `_` can occur on both sides of the `@`, but only `.` may occur at
the end of the email address, in which case it will not be considered part of
the address:

```````````````````````````````` example autolink
a.b-c_d@a.b

a.b-c_d@a.b.

a.b-c_d@a.b-

a.b-c_d@a.b_
.
<p><a href="mailto:a.b-c_d@a.b">a.b-c_d@a.b</a></p>
<p><a href="mailto:a.b-c_d@a.b">a.b-c_d@a.b</a>.</p>
<p>a.b-c_d@a.b-</p>
<p>a.b-c_d@a.b_</p>
````````````````````````````````

</div>

## Raw HTML

Text between `<` and `>` that looks like an HTML tag is parsed as a
raw HTML tag and will be rendered in HTML without escaping.
Tag and attribute names are not limited to current HTML tags,
so custom tags (and even, say, DocBook tags) may be used.

Here is the grammar for tags:

A [tag name](@) consists of an ASCII letter
followed by zero or more ASCII letters, digits, or
hyphens (`-`).

An [attribute](@) consists of [whitespace],
an [attribute name], and an optional
[attribute value specification].

An [attribute name](@)
consists of an ASCII letter, `_`, or `:`, followed by zero or more ASCII
letters, digits, `_`, `.`, `:`, or `-`.  (Note:  This is the XML
specification restricted to ASCII.  HTML5 is laxer.)

An [attribute value specification](@)
consists of optional [whitespace],
a `=` character, optional [whitespace], and an [attribute
value].

An [attribute value](@)
consists of an [unquoted attribute value],
a [single-quoted attribute value], or a [double-quoted attribute value].

An [unquoted attribute value](@)
is a nonempty string of characters not
including [whitespace], `"`, `'`, `=`, `<`, `>`, or `` ` ``.

A [single-quoted attribute value](@)
consists of `'`, zero or more
characters not including `'`, and a final `'`.

A [double-quoted attribute value](@)
consists of `"`, zero or more
characters not including `"`, and a final `"`.

An [open tag](@) consists of a `<` character, a [tag name],
zero or more [attributes], optional [whitespace], an optional `/`
character, and a `>` character.

A [closing tag](@) consists of the string `</`, a
[tag name], optional [whitespace], and the character `>`.

An [HTML comment](@) consists of `<!-->`, `<!--->`, or  `<!--`, a string of
characters not including the string `-->`, and `-->` (see the
[HTML spec](https://html.spec.whatwg.org/multipage/parsing.html#markup-declaration-open-state)).

A [processing instruction](@)
consists of the string `<?`, a string
of characters not including the string `?>`, and the string
`?>`.

A [declaration](@) consists of the
string `<!`, a name consisting of one or more uppercase ASCII letters,
[whitespace], a string of characters not including the
character `>`, and the character `>`.

A [CDATA section](@) consists of
the string `<![CDATA[`, a string of characters not including the string
`]]>`, and the string `]]>`.

An [HTML tag](@) consists of an [open tag], a [closing tag],
an [HTML comment], a [processing instruction], a [declaration],
or a [CDATA section].

Here are some simple open tags:

```````````````````````````````` example
<a><bab><c2c>
.
<p><a><bab><c2c></p>
````````````````````````````````


Empty elements:

```````````````````````````````` example
<a/><b2/>
.
<p><a/><b2/></p>
````````````````````````````````


[Whitespace] is allowed:

```````````````````````````````` example
<a  /><b2
data="foo" >
.
<p><a  /><b2
data="foo" ></p>
````````````````````````````````


With attributes:

```````````````````````````````` example
<a foo="bar" bam = 'baz <em>"</em>'
_boolean zoop:33=zoop:33 />
.
<p><a foo="bar" bam = 'baz <em>"</em>'
_boolean zoop:33=zoop:33 /></p>
````````````````````````````````


Custom tag names can be used:

```````````````````````````````` example
Foo <responsive-image src="foo.jpg" />
.
<p>Foo <responsive-image src="foo.jpg" /></p>
````````````````````````````````


Illegal tag names, not parsed as HTML:

```````````````````````````````` example
<33> <__>
.
<p>&lt;33&gt; &lt;__&gt;</p>
````````````````````````````````


Illegal attribute names:

```````````````````````````````` example
<a h*#ref="hi">
.
<p>&lt;a h*#ref=&quot;hi&quot;&gt;</p>
````````````````````````````````


Illegal attribute values:

```````````````````````````````` example
<a href="hi'> <a href=hi'>
.
<p>&lt;a href=&quot;hi'&gt; &lt;a href=hi'&gt;</p>
````````````````````````````````


Illegal [whitespace]:

```````````````````````````````` example
< a><
foo><bar/ >
<foo bar=baz
bim!bop />
.
<p>&lt; a&gt;&lt;
foo&gt;&lt;bar/ &gt;
&lt;foo bar=baz
bim!bop /&gt;</p>
````````````````````````````````


Missing [whitespace]:

```````````````````````````````` example
<a href='bar'title=title>
.
<p>&lt;a href='bar'title=title&gt;</p>
````````````````````````````````


Closing tags:

```````````````````````````````` example
</a></foo >
.
<p></a></foo ></p>
````````````````````````````````


Illegal attributes in closing tag:

```````````````````````````````` example
</a href="foo">
.
<p>&lt;/a href=&quot;foo&quot;&gt;</p>
````````````````````````````````


Comments:

```````````````````````````````` example
foo <!-- this is a --
comment - with hyphens -->
.
<p>foo <!-- this is a --
comment - with hyphens --></p>
````````````````````````````````

```````````````````````````````` example
foo <!--> foo -->

foo <!---> foo -->
.
<p>foo <!--> foo --&gt;</p>
<p>foo <!---> foo --&gt;</p>
````````````````````````````````


Processing instructions:

```````````````````````````````` example
foo <?php echo $a; ?>
.
<p>foo <?php echo $a; ?></p>
````````````````````````````````


Declarations:

```````````````````````````````` example
foo <!ELEMENT br EMPTY>
.
<p>foo <!ELEMENT br EMPTY></p>
````````````````````````````````


CDATA sections:

```````````````````````````````` example
foo <![CDATA[>&<]]>
.
<p>foo <![CDATA[>&<]]></p>
````````````````````````````````


Entity and numeric character references are preserved in HTML
attributes:

```````````````````````````````` example
foo <a href="&ouml;">
.
<p>foo <a href="&ouml;"></p>
````````````````````````````````


Backslash escapes do not work in HTML attributes:

```````````````````````````````` example
foo <a href="\*">
.
<p>foo <a href="\*"></p>
````````````````````````````````


```````````````````````````````` example
<a href="\"">
.
<p>&lt;a href=&quot;&quot;&quot;&gt;</p>
````````````````````````````````


<div class="extension">

## Disallowed Raw HTML (extension)

GFM enables the `tagfilter` extension, where the following HTML tags will be
filtered when rendering HTML output:

* `<title>`
* `<textarea>`
* `<style>`
* `<xmp>`
* `<iframe>`
* `<noembed>`
* `<noframes>`
* `<script>`
* `<plaintext>`

Filtering is done by replacing the leading `<` with the entity `&lt;`.  These
tags are chosen in particular as they change how HTML is interpreted in a way
unique to them (i.e. nested HTML is interpreted differently), and this is
usually undesireable in the context of other rendered Markdown content.

All other HTML tags are left untouched.

```````````````````````````````` example tagfilter
<strong> <title> <style> <em>

<blockquote>
  <xmp> is disallowed.  <XMP> is also disallowed.
</blockquote>
.
<p><strong> &lt;title> &lt;style> <em></p>
<blockquote>
  &lt;xmp> is disallowed.  &lt;XMP> is also disallowed.
</blockquote>
````````````````````````````````

</div>

## Hard line breaks

A line break (not in a code span or HTML tag) that is preceded
by two or more spaces and does not occur at the end of a block
is parsed as a [hard line break](@) (rendered
in HTML as a `<br />` tag):

```````````````````````````````` example
foo
baz
.
<p>foo<br />
baz</p>
````````````````````````````````


For a more visible alternative, a backslash before the
[line ending] may be used instead of two spaces:

```````````````````````````````` example
foo\
baz
.
<p>foo<br />
baz</p>
````````````````````````````````


More than two spaces can be used:

```````````````````````````````` example
foo
baz
.
<p>foo<br />
baz</p>
````````````````````````````````


Leading spaces at the beginning of the next line are ignored:

```````````````````````````````` example
foo
     bar
.
<p>foo<br />
bar</p>
````````````````````````````````


```````````````````````````````` example
foo\
     bar
.
<p>foo<br />
bar</p>
````````````````````````````````


Line breaks can occur inside emphasis, links, and other constructs
that allow inline content:

```````````````````````````````` example
*foo
bar*
.
<p><em>foo<br />
bar</em></p>
````````````````````````````````


```````````````````````````````` example
*foo\
bar*
.
<p><em>foo<br />
bar</em></p>
````````````````````````````````


Line breaks do not occur inside code spans

```````````````````````````````` example
`code
span`
.
<p><code>code   span</code></p>
````````````````````````````````


```````````````````````````````` example
`code\
span`
.
<p><code>code\ span</code></p>
````````````````````````````````


or HTML tags:

```````````````````````````````` example
<a href="foo
bar">
.
<p><a href="foo
bar"></p>
````````````````````````````````


```````````````````````````````` example
<a href="foo\
bar">
.
<p><a href="foo\
bar"></p>
````````````````````````````````


Hard line breaks are for separating inline content within a block.
Neither syntax for hard line breaks works at the end of a paragraph or
other block element:

```````````````````````````````` example
foo\
.
<p>foo\</p>
````````````````````````````````


```````````````````````````````` example
foo
.
<p>foo</p>
````````````````````````````````


```````````````````````````````` example
### foo\
.
<h3>foo\</h3>
````````````````````````````````


```````````````````````````````` example
### foo
.
<h3>foo</h3>
````````````````````````````````


## Soft line breaks

A regular line break (not in a code span or HTML tag) that is not
preceded by two or more spaces or a backslash is parsed as a
[softbreak](@).  (A softbreak may be rendered in HTML either as a
[line ending] or as a space. The result will be the same in
browsers. In the examples here, a [line ending] will be used.)

```````````````````````````````` example
foo
baz
.
<p>foo
baz</p>
````````````````````````````````


Spaces at the end of the line and beginning of the next line are
removed:

```````````````````````````````` example
foo
 baz
.
<p>foo
baz</p>
````````````````````````````````


A conforming parser may render a soft line break in HTML either as a
line break or as a space.

A renderer may also provide an option to render soft line breaks
as hard line breaks.

## Textual content

Any characters not given an interpretation by the above rules will
be parsed as plain textual content.

```````````````````````````````` example
hello $.;'there
.
<p>hello $.;'there</p>
````````````````````````````````


```````````````````````````````` example
Foo χρῆν
.
<p>Foo χρῆν</p>
````````````````````````````````


Internal spaces are preserved verbatim:

```````````````````````````````` example
Multiple     spaces
.
<p>Multiple     spaces</p>
````````````````````````````````


<!-- END TESTS -->
