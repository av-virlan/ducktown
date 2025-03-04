﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Semantics
{
    [CompilerTrait(CompilerFeature.ReadOnlyReferences)]
    public class RefEscapingTests : CompilingTestBase
    {
        [Fact]
        public void RefStructSemanticModel()
        {
            var tree = SyntaxFactory.ParseSyntaxTree(@"
using System;
struct S1 { }
ref struct S2 { public S1 F1; }
enum E1 { }
class C<T>
{
    unsafe void M<U>() where U : unmanaged
    {
        Span<int> span = default;
        var s1 = new S1();
        var s2 = new S2();
        var i0 = 0;
        var e1 = new E1();
        var o1 = new object();
        var c1 = new C<int>();
        var t1 = default(T);
        var u1 = default(U);
        void* p1 = null;
        var a1 = new { X = 0 };
        var a2 = new int[1];
        var t2 = (0, 0);
    }
}", options: TestOptions.Regular7_3);
            var comp = CreateCompilationWithSpan(tree, TestOptions.UnsafeDebugDll);
            Assert.True(comp.GetDiagnostics().All(d => d.Severity != DiagnosticSeverity.Error));
            var model = comp.GetSemanticModel(tree);
            var root = tree.GetRoot();

            Assert.True(getLocalType("span").IsRefLikeType);
            Assert.False(getLocalType("s1").IsRefLikeType);
            Assert.True(getLocalType("s2").IsRefLikeType);
            Assert.False(getLocalType("i0").IsRefLikeType);
            Assert.False(getLocalType("t1").IsRefLikeType);
            Assert.False(getLocalType("e1").IsRefLikeType);
            Assert.False(getLocalType("o1").IsRefLikeType);
            Assert.False(getLocalType("c1").IsRefLikeType);
            Assert.False(getLocalType("t1").IsRefLikeType);
            Assert.False(getLocalType("u1").IsRefLikeType);
            Assert.False(getLocalType("p1").IsRefLikeType);
            Assert.False(getLocalType("a1").IsRefLikeType);
            Assert.False(getLocalType("a2").IsRefLikeType);
            Assert.False(getLocalType("t2").IsRefLikeType);

            ITypeSymbol getLocalType(string name)
            {
                var decl = root.DescendantNodes()
                    .OfType<VariableDeclaratorSyntax>()
                    .Single(n => n.Identifier.ValueText == name);
                return ((ILocalSymbol)model.GetDeclaredSymbol(decl)).Type;
            }
        }

        [Fact]
        public void RefStructUsing()
        {
            var comp = CreateCompilationWithMscorlibAndSpan(@"
class C
{
    void M()
    {
        using (var x = GetRefStruct())
        {
        }
    }
    S2 GetRefStruct() => default;
    ref struct S2
    {
    }
}");
            comp.VerifyDiagnostics(
                // (6,16): error CS1674: 'C.S2': type used in a using statement must be implicitly convertible to 'System.IDisposable'.
                //         using (var x = GetRefStruct())
                Diagnostic(ErrorCode.ERR_NoConvToIDisp, "var x = GetRefStruct()").WithArguments("C.S2").WithLocation(6, 16));
        }

        [Fact]
        public void RefStructAnonymous()
        {
            var comp = CreateCompilationWithMscorlibAndSpan(@"
using System;
class C
{
    object M()
    {
        Span<int> outer = new Span<int>(new int[10]);
        Span<int> inner = stackalloc int[10];

        return new { Outer = outer, Inner = inner };
    }
}", options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics(
                // (10,22): error CS0828: Cannot assign 'Span<int>' to anonymous type property
                //         return new { Outer = outer, Inner = inner };
                Diagnostic(ErrorCode.ERR_AnonymousTypePropertyAssignedBadValue, "Outer = outer").WithArguments("System.Span<int>").WithLocation(10, 22),
                // (10,37): error CS0828: Cannot assign 'Span<int>' to anonymous type property
                //         return new { Outer = outer, Inner = inner };
                Diagnostic(ErrorCode.ERR_AnonymousTypePropertyAssignedBadValue, "Inner = inner").WithArguments("System.Span<int>").WithLocation(10, 37));
        }

        [Fact]
        public void RefStructInFor()
        {
            var comp = CreateCompilationWithMscorlibAndSpan(@"
using System;
class C
{
    void M()
    {
        Span<int> outer;
        for (Span<int> inner = stackalloc int[10];; inner = outer)
        {
            outer = inner;
        }
    }
}");
            comp.VerifyDiagnostics(
                // (10,21): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             outer = inner;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(10, 21));
        }

        [Fact]
        public void RefStructInLock()
        {
            var comp = CreateCompilationWithMscorlibAndSpan(@"
using System;

class C
{
    void M()
    {
        Span<int> s = stackalloc int[10];
        lock (s)
        {
        }
    }
}");
            comp.VerifyDiagnostics(
                // (9,15): error CS0185: 'Span<int>' is not a reference type as required by the lock statement
                //         lock (s)
                Diagnostic(ErrorCode.ERR_LockNeedsReference, "s").WithArguments("System.Span<int>").WithLocation(9, 15));
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefStructEscapeInIterator(LanguageVersion languageVersion)
        {
            var comp = CreateCompilationWithMscorlibAndSpan(@"
using System;
using System.Collections;
class C
{
    IEnumerable Gen()
    {
        Span<int> s = stackalloc int[10];
        yield return s;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyDiagnostics(
                // (9,22): error CS8352: Cannot use variable 's' in this context because it may expose referenced variables outside of their declaration scope
                //         yield return s;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "s").WithArguments("s").WithLocation(9, 22));
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeReturnEscape(LanguageVersion languageVersion)
        {
            var text = @"using System;


    class Program
    {
        static void Main()
        {
        }

        static ref int Test1(S1 arg)
        {
            return ref (new int[1])[0];
        }

        static S1 MayWrap(ref Span<int> arg)
        {
            return default;
        }

        static ref int Test3()
        {
            Span<int> local = stackalloc int[1];
            return ref Test1(MayWrap(ref local));
        }

        ref struct S1
        {
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyDiagnostics(
                // (23,42): error CS8526: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //             return ref Test1(MayWrap(ref local));
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(23, 42),
                // (23,30): error CS8521: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(MayWrap(ref local));
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref local)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(23, 30),
                // (23,24): error CS8521: Cannot use a result of 'Program.Test1(Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(MayWrap(ref local));
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(MayWrap(ref local))").WithArguments("Program.Test1(Program.S1)", "arg").WithLocation(23, 24)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeReturnEscape1(LanguageVersion languageVersion)
        {
            var text = @"
    using System;

    class Program
    {
        static void Main()
        {
        }

        static ref int Test1(S1 arg)
        {
            return ref (new int[1])[0];
        }

        static S1 MayWrap(Span<int> arg)
        {
            return default;
        }

        static ref int Test3()
        {
            Span<int> local = stackalloc int[1];
            var sp = MayWrap(local);
            return ref Test1(sp);
        }

        ref struct S1
        {
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (24,30): error CS8526: Cannot use variable 'sp' in this context because it may expose referenced variables outside of their declaration scope
                //             return ref Test1(sp);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sp").WithArguments("sp").WithLocation(24, 30),
                // (24,24): error CS8521: Cannot use a result of 'Program.Test1(Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(sp);
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(sp)").WithArguments("Program.Test1(Program.S1)", "arg").WithLocation(24, 24)
            );
        }

        [Fact]
        public void RefLikeReturnEscapeWithRefLikes()
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
        }

        static ref int Test1(ref S1 arg)
        {
            return ref (new int[1])[0];
        }

        static S1 MayWrap(Span<int> arg)
        {
            return default;
        }

        static ref int Test3()
        {
            Span<int> local = stackalloc int[1];
            var sp = MayWrap(local);
            return ref Test1(ref sp);    // error1
        }

        static ref int Test4()
        {
            var sp = MayWrap(default);
            return ref Test1(ref sp);    // error2
        }

        ref struct S1
        {
        }
    }
";

            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10);
            comp.VerifyDiagnostics(
                // (23,34): error CS8168: Cannot return local 'sp' by reference because it is not a ref local
                //             return ref Test1(ref sp);    // error1
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "sp").WithArguments("sp").WithLocation(23, 34),
                // (23,24): error CS8347: Cannot use a result of 'Program.Test1(ref Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(ref sp);    // error1
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(ref sp)").WithArguments("Program.Test1(ref Program.S1)", "arg").WithLocation(23, 24),
                // (29,34): error CS8168: Cannot return local 'sp' by reference because it is not a ref local
                //             return ref Test1(ref sp);    // error2
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "sp").WithArguments("sp").WithLocation(29, 34),
                // (29,24): error CS8347: Cannot use a result of 'Program.Test1(ref Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(ref sp);    // error2
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(ref sp)").WithArguments("Program.Test1(ref Program.S1)", "arg").WithLocation(29, 24)
                );

            comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular11);
            comp.VerifyDiagnostics(
                // (23,34): error CS8352: Cannot use variable 'sp' in this context because it may expose referenced variables outside of their declaration scope
                //             return ref Test1(ref sp);    // error1
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sp").WithArguments("sp").WithLocation(23, 34),
                // (23,24): error CS8347: Cannot use a result of 'Program.Test1(ref Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(ref sp);    // error1
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(ref sp)").WithArguments("Program.Test1(ref Program.S1)", "arg").WithLocation(23, 24),
                // (29,34): error CS8168: Cannot return local 'sp' by reference because it is not a ref local
                //             return ref Test1(ref sp);    // error2
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "sp").WithArguments("sp").WithLocation(29, 34),
                // (29,24): error CS8347: Cannot use a result of 'Program.Test1(ref Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(ref sp);    // error2
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(ref sp)").WithArguments("Program.Test1(ref Program.S1)", "arg").WithLocation(29, 24)
                );
        }

        [Fact]
        public void RefLikeReturnEscapeWithRefLikes1()
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
        }

        static ref Span<int> Test1(scoped ref S1 arg)
        {
            throw null;
        }

        static S1 MayWrap(Span<int> arg)
        {
            return default;
        }

        static void Test5()
        {
            var sp = MayWrap(default);

            // returnable. 
            var spR = MayWrap(Test1(ref sp));   

            Span<int> local = stackalloc int[1];
            var sp1 = MayWrap(local);

            // not returnable by value. (since it refers to a local data)
            var spNr = MayWrap(Test1(ref sp1));   

            // error
            spR = spNr;

            // ok, picks the narrowest val-escape
            var ternary =  true? spR: spNr;

            // error
            spR = ternary;
        }

        ref struct S1
        {
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text);
            comp.VerifyDiagnostics(
                // (33,19): error CS8526: Cannot use variable 'spNr' in this context because it may expose referenced variables outside of their declaration scope
                //             spR = spNr;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "spNr").WithArguments("spNr").WithLocation(33, 19),
                // (39,19): error CS8526: Cannot use variable 'ternary' in this context because it may expose referenced variables outside of their declaration scope
                //             spR = ternary;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "ternary").WithArguments("ternary").WithLocation(39, 19)
            );
        }

        [Fact]
        public void RefLikeReturnEscapeInParam()
        {
            var text = @"using System;

    class Program
    {
        static void Main()
        {
        }

        static ref int Test1(S1 arg)
        {
            return ref (new int[1])[0];
        }

        static S1 MayWrap(scoped in Span<int> arg)
        {
            return default;
        }

        static ref int Test3()
        {
            Span<int> local = stackalloc int[1];
            var sp = MayWrap(local);

            // not an error
            sp = MayWrap(local);

            // not an error
            sp = sp;

            // error here
            return ref Test1(sp);
        }

        ref struct S1
        {
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text);
            comp.VerifyDiagnostics(
                // (31,30): error CS8352: Cannot use variable 'sp' in this context because it may expose referenced variables outside of their declaration scope
                //             return ref Test1(sp);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sp").WithArguments("sp").WithLocation(31, 30),
                // (31,24): error CS8347: Cannot use a result of 'Program.Test1(Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(sp);
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(sp)").WithArguments("Program.Test1(Program.S1)", "arg").WithLocation(31, 24),
                // (28,13): warning CS1717: Assignment made to same variable; did you mean to assign something else?
                //             sp = sp;
                Diagnostic(ErrorCode.WRN_AssignmentToSelf, "sp = sp").WithLocation(28, 13)
            );
        }

        [Fact()]
        public void RefLikeReturnEscapeInParamOptional()
        {
            var text = @"
    class Program
    {
        static void Main()
        {
        }

        static ref int Test1(S1 arg)
        {
            return ref (new int[1])[0];
        }

        static S1 MayNotWrap(in int arg = 123)
        {
            return default;
        }

        static ref int Test3()
        {
            // ok
            return ref Test1(MayNotWrap());
        }

        ref struct S1
        {
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10).VerifyDiagnostics();

            // In C#11, the rvalue from a method invocation that returns a ref struct is safe-to-escape
            // from ... the ref-safe-to-escape of all ref arguments, including in arguments and default values.
            CreateCompilationWithMscorlibAndSpan(text).VerifyDiagnostics(
                // (21,24): error CS8347: Cannot use a result of 'Program.Test1(Program.S1)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(MayNotWrap());
                Diagnostic(ErrorCode.ERR_EscapeCall, "Test1(MayNotWrap())").WithArguments("Program.Test1(Program.S1)", "arg").WithLocation(21, 24),
                // (21,30): error CS8156: An expression cannot be used in this context because it may not be passed or returned by reference
                //             return ref Test1(MayNotWrap());
                Diagnostic(ErrorCode.ERR_RefReturnLvalueExpected, "MayNotWrap()").WithLocation(21, 30),
                // (21,30): error CS8347: Cannot use a result of 'Program.MayNotWrap(in int)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             return ref Test1(MayNotWrap());
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayNotWrap()").WithArguments("Program.MayNotWrap(in int)", "arg").WithLocation(21, 30));
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeScopeEscape(LanguageVersion languageVersion)
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
            Span<int> outer = default;

            S1 x = MayWrap(outer);

            {
                 Span<int> inner = stackalloc int[1];

                // valid
                x = MayWrap(outer);
    
                // error
                x = MayWrap(inner);
            }
        }

        static S1 MayWrap(Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (18,29): error CS8526: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //                 x = MayWrap(inner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(18, 29),
                // (18,21): error CS8521: Cannot use a result of 'Program.MayWrap(Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //                 x = MayWrap(inner);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(inner)").WithArguments("Program.MayWrap(System.Span<int>)", "arg").WithLocation(18, 21)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeScopeEscapeVararg(LanguageVersion languageVersion)
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {
        Span<int> outer = default;

        S1 x = MayWrap(__arglist(outer));

        {
            Span<int> inner = stackalloc int[1];

            // valid
            x = MayWrap(__arglist(outer));

            // error
            x = MayWrap(__arglist(inner));
        }
    }

    static S1 MayWrap(__arglist)
    {
        return default;
    }

    ref struct S1
    {
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (18,35): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             x = MayWrap(__arglist(inner));
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(18, 35),
                // (18,17): error CS8347: Cannot use a result of 'Program.MayWrap(__arglist)' in this context because it may expose variables referenced by parameter '__arglist' outside of their declaration scope
                //             x = MayWrap(__arglist(inner));
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(__arglist(inner))").WithArguments("Program.MayWrap(__arglist)", "__arglist").WithLocation(18, 17)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefScopeEscapeVararg(LanguageVersion languageVersion)
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {

    }    

    static ref int ReturnsRef()
    {
        int local = 42;

        // OK (also in 7.0)
        // __refvalue is not ref-returnable, so ref varargs can't come back
        return ref ReturnsRef1(__arglist(ref local));
    }

    static ref int ReturnsRef1(__arglist)
    {
        var ai = new ArgIterator(__arglist);

        // ERROR here. __refvalue is not ref-returnable
        return ref __refvalue(ai.GetNextArg(), int);
    }

    static ref int ReturnsRefSpan()
    {
        Span<int> local = stackalloc int[1];
        
        // error here;
        return ref ReturnsRefSpan1(__arglist(ref local));
    }

    static ref int ReturnsRefSpan1(__arglist)
    {
        var ai = new ArgIterator(__arglist);

        // this is ok
        return ref __refvalue(ai.GetNextArg(), Span<int>)[0];
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (24,20): error CS8156: An expression cannot be used in this context because it may not be passed or returned by reference
                //         return ref __refvalue(ai.GetNextArg(), int);
                Diagnostic(ErrorCode.ERR_RefReturnLvalueExpected, "__refvalue(ai.GetNextArg(), int)").WithLocation(24, 20),
                // (32,50): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         return ref ReturnsRefSpan1(__arglist(ref local));
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(32, 50),
                // (32,20): error CS8347: Cannot use a result of 'Program.ReturnsRefSpan1(__arglist)' in this context because it may expose variables referenced by parameter '__arglist' outside of their declaration scope
                //         return ref ReturnsRefSpan1(__arglist(ref local));
                Diagnostic(ErrorCode.ERR_EscapeCall, "ReturnsRefSpan1(__arglist(ref local))").WithArguments("Program.ReturnsRefSpan1(__arglist)", "__arglist").WithLocation(32, 20)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ThrowExpression(LanguageVersion languageVersion)
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {

    }    

    static ref int M1() => throw null;

    static ref readonly int M2() => throw null;

    static ref Span<int> M3() => throw null;

    static ref readonly Span<int> M4() => throw null;

    static Span<int> M5() => throw null;

    static Span<int> M6() => M5().Length !=0 ? M5() : throw null;

}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics();
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void UserDefinedLogical(LanguageVersion languageVersion)
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {
    }

    S1 Test()
    {
        S1 global = default;
        S1 local = stackalloc int[100];

        // ok
        local = global && local;
        local = local && local;

        // ok
        global = global && global;

        // error
        global = local && global;

        // error
        return global || local;
    }
}

ref struct S1
{
    public static implicit operator S1(Span<int> o) => default;

    public static bool operator true(S1 o) => true;
    public static bool operator false(S1 o) => false;

    public static S1 operator &(S1 x, S1 y) => x;
    public static S1 operator |(S1 x, S1 y) => x;
}

";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (22,18): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         global = local && global;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(22, 18),
                // (25,26): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         return global || local;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(25, 26)
             );
        }

        [Fact()]
        public void DiscardExpressionRef()
        {
            var text = @"

class Program
{
    static void Main()
    {

    }

    static ref int ReturnsRefTest()
    {
        return ref ReturnsRef1(out var _);
    }

    static ref int ReturnsRef1(out int x)
    {
        x = 42;
        return ref x;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10).VerifyDiagnostics(
                // (12,36): error CS8156: An expression cannot be used in this context because it may not be returned by reference
                //         return ref ReturnsRef1(out var _);
                Diagnostic(ErrorCode.ERR_RefReturnLvalueExpected, "var _").WithLocation(12, 36),
                // (12,20): error CS8347: Cannot use a result of 'Program.ReturnsRef1(out int)' in this context because it may expose variables referenced by parameter 'x' outside of their declaration scope
                //         return ref ReturnsRef1(out var _);
                Diagnostic(ErrorCode.ERR_EscapeCall, "ReturnsRef1(out var _)").WithArguments("Program.ReturnsRef1(out int)", "x").WithLocation(12, 20)
                );
            CreateCompilationWithMscorlibAndSpan(text).VerifyDiagnostics(
                // (18,20): error CS9075: Cannot return a parameter by reference 'x' because it is scoped to the current method
                //         return ref x;
                Diagnostic(ErrorCode.ERR_RefReturnScopedParameter, "x").WithArguments("x").WithLocation(18, 20)
                );
        }

        [Fact]
        public void DiscardExpressionRef_UnsafeContext()
        {
            var text = @"

unsafe class Program
{
    static ref int ReturnsRefTest()
    {
        return ref ReturnsRef1(out var _);
    }

    static ref int ReturnsRef1(out int x)
    {
        x = 42;
        return ref x;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, options: TestOptions.UnsafeDebugDll).VerifyDiagnostics(
                // (13,20): warning CS9085: This returns a parameter by reference 'x' but it is scoped to the current method
                //         return ref x;
                Diagnostic(ErrorCode.WRN_RefReturnScopedParameter, "x").WithArguments("x").WithLocation(13, 20)
                );
        }

        [Fact()]
        public void OrdinaryLocalAndOutRef()
        {
            var text = @"

class Program
{
    static void Main()
    {

    }

    static ref int ReturnsRefTest1()
    {       
        return ref ReturnsRef(out var z);
    }

    static ref int ReturnsRefTest2()
    {   int z;
        return ref ReturnsRef(out z);
    }


    static ref int ReturnsRef(out int x)
    {
        x = 42;
        return ref x;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10).VerifyDiagnostics(
                // (12,35): error CS8168: Cannot return local 'z' by reference because it is not a ref local
                //         return ref ReturnsRef(out var z);
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "var z").WithArguments("z").WithLocation(12, 35),
                // (12,20): error CS8347: Cannot use a result of 'Program.ReturnsRef(out int)' in this context because it may expose variables referenced by parameter 'x' outside of their declaration scope
                //         return ref ReturnsRef(out var z);
                Diagnostic(ErrorCode.ERR_EscapeCall, "ReturnsRef(out var z)").WithArguments("Program.ReturnsRef(out int)", "x").WithLocation(12, 20),
                // (17,35): error CS8168: Cannot return local 'z' by reference because it is not a ref local
                //         return ref ReturnsRef(out z);
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "z").WithArguments("z").WithLocation(17, 35),
                // (17,20): error CS8347: Cannot use a result of 'Program.ReturnsRef(out int)' in this context because it may expose variables referenced by parameter 'x' outside of their declaration scope
                //         return ref ReturnsRef(out z);
                Diagnostic(ErrorCode.ERR_EscapeCall, "ReturnsRef(out z)").WithArguments("Program.ReturnsRef(out int)", "x").WithLocation(17, 20)
                );
            CreateCompilationWithMscorlibAndSpan(text).VerifyDiagnostics(
                // (24,20): error CS9075: Cannot return a parameter by reference 'x' because it is scoped to the current method
                //         return ref x;
                Diagnostic(ErrorCode.ERR_RefReturnScopedParameter, "x").WithArguments("x").WithLocation(24, 20)
                );
        }

        [Fact()]
        public void DiscardExpressionSpan()
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {

    }

    static Span<int> Test1()
    {
        var s = ReturnsSpan(out var _);

        // ok
        return s;
    }

    static Span<int> Test2()
    {
        ref var s = ref ReturnsSpan(out var _);

        // error
        s = stackalloc int[1];

        // ok
        return s;
    }

    static void Test3()
    {
        // error
        ReturnsSpan(out var _ ) = stackalloc int[1];
    }

    static ref Span<int> ReturnsSpan(out Span<int> x)
    {
        x = default;
        return ref x;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10).VerifyDiagnostics(
                // (23,13): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         s = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(23, 13),
                // (32,35): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         ReturnsSpan(out var _ ) = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(32, 35)
                );
            CreateCompilationWithMscorlibAndSpan(text).VerifyDiagnostics(
                // (23,13): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         s = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(23, 13),
                // (32,35): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         ReturnsSpan(out var _ ) = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(32, 35),
                // (38,20): error CS9075: Cannot return a parameter by reference 'x' because it is scoped to the current method
                //         return ref x;
                Diagnostic(ErrorCode.ERR_RefReturnScopedParameter, "x").WithArguments("x").WithLocation(38, 20)
                );
        }

        [Fact]
        public void DiscardExpressionSpan_UnsafeContext()
        {
            var text = @"
using System;
unsafe class Program
{
    static Span<int> Test1()
    {
        var s = ReturnsSpan(out var _);
        return s;
    }

    static Span<int> Test2()
    {
        ref var s = ref ReturnsSpan(out var _);
        s = stackalloc int[1]; // 1
        return s;
    }

    static void Test3()
    {
        ReturnsSpan(out var _ ) = stackalloc int[1]; // 2
    }

    static ref Span<int> ReturnsSpan(out Span<int> x)
    {
        x = default;
        return ref x; // 3
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics(
                // (14,13): warning CS9078: A result of a stackalloc expression of type 'Span<int>' in this context may be exposed outside of the containing method
                //         s = stackalloc int[1]; // 1
                Diagnostic(ErrorCode.WRN_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(14, 13),
                // (20,35): warning CS9078: A result of a stackalloc expression of type 'Span<int>' in this context may be exposed outside of the containing method
                //         ReturnsSpan(out var _ ) = stackalloc int[1]; // 2
                Diagnostic(ErrorCode.WRN_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(20, 35),
                // (26,20): warning CS9085: This returns a parameter by reference 'x' but it is scoped to the current method
                //         return ref x; // 3
                Diagnostic(ErrorCode.WRN_RefReturnScopedParameter, "x").WithArguments("x").WithLocation(26, 20)
                );
            var verifier = CompileAndVerify(comp, verify: Verification.Skipped);
            verifier.VerifyIL("Program.Test2", @"
{
  // Code size       38 (0x26)
  .maxstack  2
  .locals init (System.Span<int>& V_0, //s
                System.Span<int> V_1,
                System.Span<int> V_2,
                System.Span<int> V_3)
  IL_0000:  nop
  IL_0001:  ldloca.s   V_1
  IL_0003:  call       ""ref System.Span<int> Program.ReturnsSpan(out System.Span<int>)""
  IL_0008:  stloc.0
  IL_0009:  ldc.i4.4
  IL_000a:  conv.u
  IL_000b:  localloc
  IL_000d:  ldc.i4.1
  IL_000e:  newobj     ""System.Span<int>..ctor(void*, int)""
  IL_0013:  stloc.2
  IL_0014:  ldloc.0
  IL_0015:  ldloc.2
  IL_0016:  stobj      ""System.Span<int>""
  IL_001b:  ldloc.0
  IL_001c:  ldobj      ""System.Span<int>""
  IL_0021:  stloc.3
  IL_0022:  br.s       IL_0024
  IL_0024:  ldloc.3
  IL_0025:  ret
}
");
            verifier.VerifyIL("Program.Test3", @"
{
  // Code size       28 (0x1c)
  .maxstack  2
  .locals init (System.Span<int> V_0,
                System.Span<int>& V_1,
                System.Span<int> V_2)
  IL_0000:  nop
  IL_0001:  ldloca.s   V_0
  IL_0003:  call       ""ref System.Span<int> Program.ReturnsSpan(out System.Span<int>)""
  IL_0008:  stloc.1
  IL_0009:  ldc.i4.4
  IL_000a:  conv.u
  IL_000b:  localloc
  IL_000d:  ldc.i4.1
  IL_000e:  newobj     ""System.Span<int>..ctor(void*, int)""
  IL_0013:  stloc.2
  IL_0014:  ldloc.1
  IL_0015:  ldloc.2
  IL_0016:  stobj      ""System.Span<int>""
  IL_001b:  ret
}
");
        }

        [Fact()]
        public void OrdinaryLocalAndOutSpan()
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {

    }

    static Span<int> Test1()
    {
        var s = ReturnsSpan(out var z);

        // ok
        return s;
    }

    static Span<int> Test2()
    {
        ref var r = ref ReturnsSpan(out var z);

        // error
        r = stackalloc int[1];

        // ok
        return r;
    }

    static void Test3()
    {
        ReturnsSpan(out var z) = stackalloc int[1];
    }

    static Span<int> Test4()
    {
        Span<int> s;
        var r = ReturnsSpan(out s);

        // ok
        return r;
    }

    static Span<int> Test5()
    {
        Span<int> s;
        ref var r = ref ReturnsSpan(out s);

        // error
        r = stackalloc int[1];

        // ok
        return r;
    }

    static void Test6()
    {
        Span<int> s;

        // error
        ReturnsSpan(out s) = stackalloc int[1];
    }

    static ref Span<int> ReturnsSpan(out Span<int> x)
    {
        x = default;
        return ref x;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10).VerifyDiagnostics(
                // (23,13): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         r = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(23, 13),
                // (31,34): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         ReturnsSpan(out var z) = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(31, 34),
                // (49,13): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         r = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(49, 13),
                // (60,30): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         ReturnsSpan(out s) = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(60, 30)
                );
            CreateCompilationWithMscorlibAndSpan(text).VerifyDiagnostics(
                // (23,13): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         r = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(23, 13),
                // (31,34): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         ReturnsSpan(out var z) = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(31, 34),
                // (49,13): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         r = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(49, 13),
                // (60,30): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //         ReturnsSpan(out s) = stackalloc int[1];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[1]").WithArguments("System.Span<int>").WithLocation(60, 30),
                // (66,20): error CS9075: Cannot return a parameter by reference 'x' because it is scoped to the current method
                //         return ref x;
                Diagnostic(ErrorCode.ERR_RefReturnScopedParameter, "x").WithArguments("x").WithLocation(66, 20)
                );
        }

        [Fact]
        public void RefLikeScopeEscapeReturnable()
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
            Span<int> outer = default;

            // make x returnable
            S1 x = default;

            {
                Span<int> inner = stackalloc int[0];

                // valid
                x = MayWrap(ref outer);
    
                // error
                x = MayWrap(ref inner);
            }
        }

        static S1 MayWrap(ref Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10);
            comp.VerifyDiagnostics(
                // (19,33): error CS8526: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //                 x = MayWrap(ref inner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(19, 33),
                // (19,21): error CS8521: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //                 x = MayWrap(ref inner);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref inner)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(19, 21)
            );

            comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular11);
            comp.VerifyDiagnostics(
                // (16,33): error CS8168: Cannot return local 'outer' by reference because it is not a ref local
                //                 x = MayWrap(ref outer);
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "outer").WithArguments("outer").WithLocation(16, 33),
                // (16,21): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //                 x = MayWrap(ref outer);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref outer)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(16, 21),
                // (19,33): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //                 x = MayWrap(ref inner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(19, 33),
                // (19,21): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //                 x = MayWrap(ref inner);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref inner)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(19, 21)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeScopeEscapeThis(LanguageVersion languageVersion)
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
            Span<int> outer = default;

            S1 x = MayWrap(ref outer);

            {
                Span<int> inner = stackalloc int[1];

                // valid
                x = S1.NotSlice(1);

                // valid
                x = MayWrap(ref outer).Slice(1);
    
                // error
                x = MayWrap(ref inner).Slice(1);
            }
        }

        static S1 MayWrap(ref Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
            public static S1 NotSlice(int x) => default;

            public S1 Slice(int x) => this;
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyDiagnostics(
                // (21,33): error CS8526: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //                 x = MayWrap(ref inner).Slice(1);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(21, 33),
                // (21,21): error CS8521: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //                 x = MayWrap(ref inner).Slice(1);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref inner)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(21, 21)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeScopeEscapeThisRef(LanguageVersion languageVersion)
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {
        Span<int> outer = default;

        ref S1 x = ref MayWrap(ref outer)[0];

        {
            Span<int> inner = stackalloc int[1];

            // valid
            x[0] = MayWrap(ref outer).Slice(1)[0];

            // error, technically rules for this case can be relaxed, 
            // but ref-like typed ref-returning properties are nearly impossible to implement in a useful way
            //
            x[0] = MayWrap(ref inner).Slice(1)[0];

            // error, technically rules for this case can be relaxed, 
            // but ref-like typed ref-returning properties are nearly impossible to implement in a useful way
            //
            x[x] = MayWrap(ref inner).Slice(1)[0];

            // error
            x.ReturnsRefArg(ref x) = MayWrap(ref inner).Slice(1)[0];
        }
    }

    static S1 MayWrap(ref Span<int> arg)
    {
        return default;
    }

    ref struct S1
    {
        public ref S1 this[int i] => throw null;

        public ref S1 this[S1 i] => throw null;

        public ref S1 ReturnsRefArg(ref S1 arg) => throw null;

        public S1 Slice(int x) => this;
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(new[] { text, UnscopedRefAttributeDefinition }, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyDiagnostics(
                // (20,32): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             x[0] = MayWrap(ref inner).Slice(1)[0];
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(20, 32),
                // (20,20): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             x[0] = MayWrap(ref inner).Slice(1)[0];
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref inner)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(20, 20),
                // (25,32): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             x[x] = MayWrap(ref inner).Slice(1)[0];
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(25, 32),
                // (25,20): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             x[x] = MayWrap(ref inner).Slice(1)[0];
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref inner)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(25, 20),
                // (28,50): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             x.ReturnsRefArg(ref x) = MayWrap(ref inner).Slice(1)[0];
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(28, 50),
                // (28,38): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             x.ReturnsRefArg(ref x) = MayWrap(ref inner).Slice(1)[0];
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref inner)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(28, 38));
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeScopeEscapeField(LanguageVersion languageVersion)
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {
        Span<int> outer = default;

        S1 x = MayWrap(outer);

        {
            Span<int> inner = stackalloc int[1];

            // valid
            x.field = MayWrap(outer).Slice(1).field;

            // error
            x.field = MayWrap(inner).Slice(1).field;
        }
    }

    static S1 MayWrap(Span<int> arg)
    {
        return default;
    }

    ref struct S1
    {
        public S0 field;

        public S1 Slice(int x) => this;
    }

    ref struct S0
    {
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (18,31): error CS8526: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             x.field = MayWrap(inner).Slice(1).field;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(18, 31),
                // (18,23): error CS8521: Cannot use a result of 'Program.MayWrap(Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             x.field = MayWrap(inner).Slice(1).field;
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(inner)").WithArguments("Program.MayWrap(System.Span<int>)", "arg").WithLocation(18, 23)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeEscapeParamsAndTopLevel(LanguageVersion languageVersion)
        {
            var text = @"
    class Program
    {
        static void Main()
        {
        }

        void Test1(int x)
        {
            int y = 1;

            var rx = MayWrap(ref x);
            var ry = MayWrap(ref y);

            // valid. parameter scope and the top local scope are the same.
            rx = ry;
            
            bool condition = true;
            rx = condition ? rx: ry;
        }

        static S1 MayWrap(ref int arg)
        {
            return default;
        }

        ref struct S1
        {
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
            // no diagnostics expected
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeEscapeMixingCallSameArgValue(LanguageVersion languageVersion)
        {
            var text = @"
    using System;
    public class Program
    {
        static void Main()
        {
        }

        void Test1()
        {
            S1 rOuter = default;

            Span<int> inner = stackalloc int[1];
            S1 rInner = MayWrap(inner);

            // valid
            MayAssign(ref rOuter);

            // valid
            MayAssign(ref rInner);
        }

        static void MayAssign(ref S1 arg1)
        {
            // valid
            arg1 = MayWrap(arg1.field);
        }

        static S1 MayWrap(Span<int> arg)
        {
            return default;
        }

        public ref struct S1
        {
            public Span<int> field;
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics();
        }

        [Fact]
        public void RefLikeEscapeMixingCall()
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
        }

        void Test1()
        {
            S1 rOuter = default;

            Span<int> inner = stackalloc int[1];
            S1 rInner = MayWrap(ref inner);

            // valid
            MayAssign(ref rOuter, ref rOuter);

            // error
            MayAssign(ref rOuter, ref rInner);

            // error
            MayAssign(ref inner, ref rOuter);
        }

        static void MayAssign(ref Span<int> arg1, ref S1 arg2)
        {
            arg2 = MayWrap(ref arg1);
        }

        static void MayAssign(ref S1 arg1, ref S1 arg2)
        {
            arg1 = arg2;
        }

        static S1 MayWrap(ref Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10);
            comp.VerifyDiagnostics(
                // (20,39): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssign(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(20, 39),
                // (20,13): error CS8350: This combination of arguments to 'Program.MayAssign(ref Program.S1, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg2' outside of their declaration scope
                //             MayAssign(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign(ref rOuter, ref rInner)").WithArguments("Program.MayAssign(ref Program.S1, ref Program.S1)", "arg2").WithLocation(20, 13),
                // (23,27): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssign(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(23, 27),
                // (23,13): error CS8350: This combination of arguments to 'Program.MayAssign(ref Span<int>, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //             MayAssign(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign(ref inner, ref rOuter)").WithArguments("Program.MayAssign(ref System.Span<int>, ref Program.S1)", "arg1").WithLocation(23, 13));

            comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular11);
            comp.VerifyDiagnostics(
                // (20,39): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssign(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(20, 39),
                // (20,13): error CS8350: This combination of arguments to 'Program.MayAssign(ref Program.S1, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg2' outside of their declaration scope
                //             MayAssign(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign(ref rOuter, ref rInner)").WithArguments("Program.MayAssign(ref Program.S1, ref Program.S1)", "arg2").WithLocation(20, 13),
                // (23,27): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssign(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(23, 27),
                // (23,13): error CS8350: This combination of arguments to 'Program.MayAssign(ref Span<int>, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //             MayAssign(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign(ref inner, ref rOuter)").WithArguments("Program.MayAssign(ref System.Span<int>, ref Program.S1)", "arg1").WithLocation(23, 13),
                // (28,32): error CS9077: Cannot return a parameter by reference 'arg1' through a ref parameter; it can only be returned in a return statement
                //             arg2 = MayWrap(ref arg1);
                Diagnostic(ErrorCode.ERR_RefReturnOnlyParameter, "arg1").WithArguments("arg1").WithLocation(28, 32),
                // (28,20): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             arg2 = MayWrap(ref arg1);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref arg1)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(28, 20)
            );
        }

        [Fact]
        public void RefLikeEscapeMixingCall_UnsafeContext()
        {
            var text = @"
using System;
class Program
{
    unsafe void Test1()
    {
        S1 rOuter = default;

        Span<int> inner = stackalloc int[1];
        S1 rInner = MayWrap(ref inner);

        // valid
        MayAssign(ref rOuter, ref rOuter);

        // warn
        MayAssign(ref rOuter, ref rInner);

        // warn
        MayAssign(ref inner, ref rOuter);
    }

    static unsafe void MayAssign(ref Span<int> arg1, ref S1 arg2)
    {
        arg2 = MayWrap(ref arg1);
    }

    static void MayAssign(ref S1 arg1, ref S1 arg2)
    {
        arg1 = arg2;
    }

    static S1 MayWrap(ref Span<int> arg)
    {
        return default;
    }

    ref struct S1
    {
    }
}
";

            var comp = CreateCompilationWithMscorlibAndSpan(text, options: TestOptions.UnsafeDebugDll, parseOptions: TestOptions.Regular10);
            comp.VerifyDiagnostics(
                // (16,35): warning CS9080: Use of variable 'rInner' in this context may expose referenced variables outside of their declaration scope
                //         MayAssign(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.WRN_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(16, 35),
                // (19,23): warning CS9080: Use of variable 'inner' in this context may expose referenced variables outside of their declaration scope
                //         MayAssign(ref inner, ref rOuter);
                Diagnostic(ErrorCode.WRN_EscapeVariable, "inner").WithArguments("inner").WithLocation(19, 23)
                );

            comp = CreateCompilationWithMscorlibAndSpan(text, options: TestOptions.UnsafeDebugDll, parseOptions: TestOptions.Regular11);
            comp.VerifyDiagnostics(
                // (16,35): warning CS9080: Use of variable 'rInner' in this context may expose referenced variables outside of their declaration scope
                //         MayAssign(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.WRN_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(16, 35),
                // (19,23): warning CS9080: Use of variable 'inner' in this context may expose referenced variables outside of their declaration scope
                //         MayAssign(ref inner, ref rOuter);
                Diagnostic(ErrorCode.WRN_EscapeVariable, "inner").WithArguments("inner").WithLocation(19, 23),
                // (24,28): warning CS9094: This returns a parameter by reference 'arg1' through a ref parameter; but it can only safely be returned in a return statement
                //         arg2 = MayWrap(ref arg1);
                Diagnostic(ErrorCode.WRN_RefReturnOnlyParameter, "arg1").WithArguments("arg1").WithLocation(24, 28)
                );
        }

        [Fact]
        public void RefLikeEscapeMixingCallVararg()
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {
    }

    void Test1()
    {
        S1 rOuter = default;

        Span<int> inner = stackalloc int[1];
        S1 rInner = MayWrap(ref inner);

        // valid
        MayAssign2(__arglist(ref rOuter, ref rOuter));

        // error
        MayAssign2(__arglist(ref rOuter, ref rInner));

        // error
        MayAssign1(__arglist(ref inner, ref rOuter));
    }

    static void MayAssign1(__arglist)
    {
        var ai = new ArgIterator(__arglist);

        ref var arg1 = ref __refvalue(ai.GetNextArg(), Span<int>);
        ref var arg2 = ref __refvalue(ai.GetNextArg(), S1);

        arg2 = MayWrap(ref arg1);
    }

    static void MayAssign2(__arglist)
    {
        var ai = new ArgIterator(__arglist);

        ref var arg1 = ref __refvalue(ai.GetNextArg(), S1);
        ref var arg2 = ref __refvalue(ai.GetNextArg(), S1);

        arg1 = arg2;
    }

    static S1 MayWrap(scoped ref Span<int> arg)
    {
        return default;
    }

    ref struct S1
    {
    }
}
";

            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10).VerifyDiagnostics(
                // (46,23): error CS8936: Feature 'ref fields' is not available in C# 10.0. Please use language version 11.0 or greater.
                //     static S1 MayWrap(scoped ref Span<int> arg)
                Diagnostic(ErrorCode.ERR_FeatureNotAvailableInVersion10, "scoped").WithArguments("ref fields", "11.0").WithLocation(46, 23),
                // (20,46): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //         MayAssign2(__arglist(ref rOuter, ref rInner));
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(20, 46),
                // (20,9): error CS8350: This combination of arguments to 'Program.MayAssign2(__arglist)' is disallowed because it may expose variables referenced by parameter '__arglist' outside of their declaration scope
                //         MayAssign2(__arglist(ref rOuter, ref rInner));
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign2(__arglist(ref rOuter, ref rInner))").WithArguments("Program.MayAssign2(__arglist)", "__arglist").WithLocation(20, 9),
                // (23,34): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         MayAssign1(__arglist(ref inner, ref rOuter));
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(23, 34),
                // (23,9): error CS8350: This combination of arguments to 'Program.MayAssign1(__arglist)' is disallowed because it may expose variables referenced by parameter '__arglist' outside of their declaration scope
                //         MayAssign1(__arglist(ref inner, ref rOuter));
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign1(__arglist(ref inner, ref rOuter))").WithArguments("Program.MayAssign1(__arglist)", "__arglist").WithLocation(23, 9)
            );

            // Same errors modulo the scoped
            CreateCompilationWithMscorlibAndSpan(text).VerifyDiagnostics(
                // (20,46): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //         MayAssign2(__arglist(ref rOuter, ref rInner));
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(20, 46),
                // (20,9): error CS8350: This combination of arguments to 'Program.MayAssign2(__arglist)' is disallowed because it may expose variables referenced by parameter '__arglist' outside of their declaration scope
                //         MayAssign2(__arglist(ref rOuter, ref rInner));
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign2(__arglist(ref rOuter, ref rInner))").WithArguments("Program.MayAssign2(__arglist)", "__arglist").WithLocation(20, 9),
                // (23,34): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         MayAssign1(__arglist(ref inner, ref rOuter));
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(23, 34),
                // (23,9): error CS8350: This combination of arguments to 'Program.MayAssign1(__arglist)' is disallowed because it may expose variables referenced by parameter '__arglist' outside of their declaration scope
                //         MayAssign1(__arglist(ref inner, ref rOuter));
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssign1(__arglist(ref inner, ref rOuter))").WithArguments("Program.MayAssign1(__arglist)", "__arglist").WithLocation(23, 9)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeEscapeMixingIndex(LanguageVersion languageVersion)
        {
            var text = @"
class Program
{
    static void Main()
    {
    }

    void Test1()
    {
        S1 rOuter = default;

        int inner = 1;
        S1 rInner = MayWrap(ref inner);

        // valid
        int dummy1 = this[rOuter, rOuter];

        // error
        int dummy2 = this[rOuter, rInner];

        // error
        int dummy3 = this[inner, rOuter];
    }

    int this[in int arg1, in S1 arg2]
    {
        get
        {
            // not possible
            // arg2 = MayWrap(ref arg1);
            return 0;
        }
    }

    int this[in S1 arg1, in S1 arg2]
    {
        get
        {
            // not possible
            // arg1 = arg2;
            return 0;
        }
    }

    static S1 MayWrap(ref int arg)
    {
        return default;
    }

    ref struct S1
    {
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
            // no diagnostics
            );
        }

        [Fact]
        public void RefLikeEscapeMixingIndexOnRefLike()
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {
    }

    void Test1()
    {
        S1 rOuter = default;
        rOuter.field = default;

        Span<int> inner = stackalloc int[1];
        S1 rInner = MayWrap(inner);

        // valid
        int dummy1 = rOuter[rOuter];

        // valid
        int dummy2 = rInner[rOuter];

        // error
        int dummy3 = rOuter[rInner];

        // error
        int dummy4 = rOuter[inner];
    }

    static S1 MayWrap(in Span<int> arg)
    {
        return default;
    }

    ref struct S1
    {
        public Span<int> field;

        public int this[in Span<int> arg1]
        {
            get
            {
                // error: ref-safe-to-escape of arg1 is return-only
                this = MayWrap(arg1);
                return 0;
            }
        }

        public int this[in S1 arg1]
        {
            get
            {
                // error: ref-safe-to-escape of arg1 is return-only
                this = MayWrap(arg1.field);

                // ok: safe-to-escape of arg1 is calling method
                this = arg1;

                return 0;
            }
        }
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10);
            comp.VerifyDiagnostics(
                // (24,29): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //         int dummy3 = rOuter[rInner];
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(24, 29),
                // (24,22): error CS8350: This combination of arguments to 'Program.S1.this[in Program.S1]' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //         int dummy3 = rOuter[rInner];
                Diagnostic(ErrorCode.ERR_CallArgMixing, "rOuter[rInner]").WithArguments("Program.S1.this[in Program.S1]", "arg1").WithLocation(24, 22),
                // (27,29): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         int dummy4 = rOuter[inner];
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(27, 29),
                // (27,22): error CS8350: This combination of arguments to 'Program.S1.this[in Span<int>]' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //         int dummy4 = rOuter[inner];
                Diagnostic(ErrorCode.ERR_CallArgMixing, "rOuter[inner]").WithArguments("Program.S1.this[in System.Span<int>]", "arg1").WithLocation(27, 22)
            );

            comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular11);
            comp.VerifyDiagnostics(
                // (24,29): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //         int dummy3 = rOuter[rInner];
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(24, 29),
                // (24,22): error CS8350: This combination of arguments to 'Program.S1.this[in Program.S1]' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //         int dummy3 = rOuter[rInner];
                Diagnostic(ErrorCode.ERR_CallArgMixing, "rOuter[rInner]").WithArguments("Program.S1.this[in Program.S1]", "arg1").WithLocation(24, 22),
                // (27,29): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         int dummy4 = rOuter[inner];
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(27, 29),
                // (27,22): error CS8350: This combination of arguments to 'Program.S1.this[in Span<int>]' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //         int dummy4 = rOuter[inner];
                Diagnostic(ErrorCode.ERR_CallArgMixing, "rOuter[inner]").WithArguments("Program.S1.this[in System.Span<int>]", "arg1").WithLocation(27, 22),
                // (44,32): error CS9077: Cannot return a parameter by reference 'arg1' through a ref parameter; it can only be returned in a return statement
                //                 this = MayWrap(arg1);
                Diagnostic(ErrorCode.ERR_RefReturnOnlyParameter, "arg1").WithArguments("arg1").WithLocation(44, 32),
                // (44,24): error CS8347: Cannot use a result of 'Program.MayWrap(in Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //                 this = MayWrap(arg1);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(arg1)").WithArguments("Program.MayWrap(in System.Span<int>)", "arg").WithLocation(44, 24),
                // (54,32): error CS9078: Cannot return by reference a member of parameter 'arg1' through a ref parameter; it can only be returned in a return statement
                //                 this = MayWrap(arg1.field);
                Diagnostic(ErrorCode.ERR_RefReturnOnlyParameter2, "arg1").WithArguments("arg1").WithLocation(54, 32),
                // (54,24): error CS8347: Cannot use a result of 'Program.MayWrap(in Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //                 this = MayWrap(arg1.field);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(arg1.field)").WithArguments("Program.MayWrap(in System.Span<int>)", "arg").WithLocation(54, 24)
            );
        }

        [Fact]
        public void RefLikeEscapeMixingCtor()
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
        }

        delegate void D1(ref S1 arg1, ref S1 arg2);
        delegate void D2(ref Span<int> arg1, ref S1 arg2);

        void Test1()
        {
            S1 rOuter = default;

            Span<int> inner = stackalloc int[1];
            S1 rInner = MayWrap(ref inner);

            D1 MayAssignDel1 = MayAssign;
            D2 MayAssignDel2 = MayAssign;

            // valid
            MayAssignDel1(ref rOuter, ref rOuter);

            // error
            MayAssignDel1(ref rOuter, ref rInner);

            // error
            MayAssignDel2(ref inner, ref rOuter);
        }

        static void MayAssign(ref Span<int> arg1, ref S1 arg2)
        {
            arg2 = MayWrap(ref arg1);
        }

        static void MayAssign(ref S1 arg1, ref S1 arg2)
        {
            arg1 = arg2;
        }

        static S1 MayWrap(ref Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10);
            comp.VerifyDiagnostics(
                // (26,43): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssignDel1(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(26, 43),
                // (26,13): error CS8350: This combination of arguments to 'Program.D1.Invoke(ref Program.S1, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg2' outside of their declaration scope
                //             MayAssignDel1(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssignDel1(ref rOuter, ref rInner)").WithArguments("Program.D1.Invoke(ref Program.S1, ref Program.S1)", "arg2").WithLocation(26, 13),
                // (29,31): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssignDel2(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(29, 31),
                // (29,13): error CS8350: This combination of arguments to 'Program.D2.Invoke(ref Span<int>, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //             MayAssignDel2(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssignDel2(ref inner, ref rOuter)").WithArguments("Program.D2.Invoke(ref System.Span<int>, ref Program.S1)", "arg1").WithLocation(29, 13)
            );

            comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular11);
            comp.VerifyDiagnostics(
                // (26,43): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssignDel1(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(26, 43),
                // (26,13): error CS8350: This combination of arguments to 'Program.D1.Invoke(ref Program.S1, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg2' outside of their declaration scope
                //             MayAssignDel1(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssignDel1(ref rOuter, ref rInner)").WithArguments("Program.D1.Invoke(ref Program.S1, ref Program.S1)", "arg2").WithLocation(26, 13),
                // (29,31): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             MayAssignDel2(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(29, 31),
                // (29,13): error CS8350: This combination of arguments to 'Program.D2.Invoke(ref Span<int>, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //             MayAssignDel2(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "MayAssignDel2(ref inner, ref rOuter)").WithArguments("Program.D2.Invoke(ref System.Span<int>, ref Program.S1)", "arg1").WithLocation(29, 13),
                // (34,32): error CS9077: Cannot return a parameter by reference 'arg1' through a ref parameter; it can only be returned in a return statement
                //             arg2 = MayWrap(ref arg1);
                Diagnostic(ErrorCode.ERR_RefReturnOnlyParameter, "arg1").WithArguments("arg1").WithLocation(34, 32),
                // (34,20): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             arg2 = MayWrap(ref arg1);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref arg1)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(34, 20)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeObjInitializers(LanguageVersion languageVersion)
        {
            var text = @"
    using System;

    class Program
    {
        static void Main()
        {
        }

        static S2 Test1()
        {
            S1 outer = default;
            S1 inner = stackalloc int[1];

            // error
            return new S2() { Field1 = outer, Field2 = inner };
        }

        static S2 Test2()
        {
            S1 outer = default;
            S1 inner = stackalloc int[1];

            S2 result;

            // error
            result = new S2() { Field1 = inner, Field2 = outer };

            return result;
        }

        static S2 Test3()
        {
            S1 outer = default;
            S1 inner = stackalloc int[1];

            return new S2() { Field1 = outer, Field2 = outer };
        }

        public ref struct S1
        {
            public static implicit operator S1(Span<int> o) => default;
        }

        public ref struct S2
        {
            public S1 Field1;
            public S1 Field2;
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (16,47): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             return new S2() { Field1 = outer, Field2 = inner };
                Diagnostic(ErrorCode.ERR_EscapeVariable, "Field2 = inner").WithArguments("inner").WithLocation(16, 47),
                // (27,33): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             result = new S2() { Field1 = inner, Field2 = outer };
                Diagnostic(ErrorCode.ERR_EscapeVariable, "Field1 = inner").WithArguments("inner").WithLocation(27, 33)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeObjInitializers1(LanguageVersion languageVersion)
        {
            var text = @"
    using System;

    class Program
    {
        static void Main()
        {
        }

        static S2 Test1()
        {
            S1 outer = default;
            S1 inner = stackalloc int[1];

            var x1 = new S2() { Field1 = outer, Field2 = inner };

            // error
            return x1;
        }

        static S2 Test2()
        {
            S1 outer = default;
            S1 inner = stackalloc int[1];

            var x2 = new S2() { Field1 = inner, Field2 = outer };

            // error
            return x2;
        }

        static S2 Test3()
        {
            S1 outer = default;
            S1 inner = stackalloc int[1];

            var x3 = new S2() { Field1 = outer, Field2 = outer };

            // ok
            return x3;
        }

        public ref struct S1
        {
            public static implicit operator S1(Span<int> o) => default;
        }

        public ref struct S2
        {
            public S1 Field1;
            public S1 Field2;
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (18,20): error CS8352: Cannot use variable 'x1' in this context because it may expose referenced variables outside of their declaration scope
                //             return x1;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "x1").WithArguments("x1").WithLocation(18, 20),
                // (29,20): error CS8352: Cannot use variable 'x2' in this context because it may expose referenced variables outside of their declaration scope
                //             return x2;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "x2").WithArguments("x2").WithLocation(29, 20)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeObjInitializersIndexer(LanguageVersion languageVersion)
        {
            var text = @"
using System;

class Program
{
    static void Main()
    {
    }

    static S2 Test1()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        // error
        return new S2() { [inner] = outer, Field2 = outer };
    }

    static S2 Test2()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        // error
        return new S2() { [outer] = inner, Field2 = outer };
    }

    static S2 Test3()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        return new S2() { [outer] = outer, Field2 = outer };
    }

    public ref struct S1
    {
        public static implicit operator S1(Span<int> o) => default;
    }

    public ref struct S2
    {
        private S1 field;

        public S1 this[S1 i]
        {
            get
            {
                return i;
            }
            set
            {
                field = i;
            }
        }

        public S1 Field2;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (16,28): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         return new S2() { [inner] = outer, Field2 = outer };
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(16, 28),
                // (25,27): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         return new S2() { [outer] = inner, Field2 = outer };
                Diagnostic(ErrorCode.ERR_EscapeVariable, "[outer] = inner").WithArguments("inner").WithLocation(25, 27)
                );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeObjInitializersIndexer1(LanguageVersion languageVersion)
        {
            var text = @"
using System;

class Program
{
    static void Main()
    {
    }

    static S2 Test1()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        var x1 =  new S2() { [inner] = outer, Field2 = outer };

        // error
        return x1;
    }

    static S2 Test2()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        S2 result;

        // error
        result = new S2() { [outer] = inner, Field2 = outer };

        return result;
    }

    static S2 Test3()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        var x3 = new S2() { [outer] = outer, Field2 = outer };

        // ok
        return x3;
    }

    public ref struct S1
    {
        public static implicit operator S1(Span<int> o) => default;
    }

    public ref struct S2
    {
        private S1 field;

        public S1 this[S1 i]
        {
            get
            {
                return i;
            }
            set
            {
                field = i;
            }
        }

        public S1 Field2;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (18,16): error CS8352: Cannot use variable 'x1' in this context because it may expose referenced variables outside of their declaration scope
                //         return x1;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "x1").WithArguments("x1").WithLocation(18, 16),
                // (29,29): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         result = new S2() { [outer] = inner, Field2 = outer };
                Diagnostic(ErrorCode.ERR_EscapeVariable, "[outer] = inner").WithArguments("inner").WithLocation(29, 29)
                );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeObjInitializersNested(LanguageVersion languageVersion)
        {
            var text = @"
using System;

class Program
{
    static void Main()
    {
    }

    static S2 Nested1()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        return new S2() { Field2 = {[inner] = outer} };
    }

    static S2 Nested2()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        var x = new S2() { Field2 = {[inner] = outer } };

        return x;
    }

    static S2 Nested3()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        return new S2() { Field2 = {[outer] = inner} };
    }

    static S2 Nested4()
    {
        S1 outer = default;
        S1 inner = stackalloc int[1];

        var x = new S2() { Field2 = {[outer] = outer } };

        return x;  //ok
    }

    public ref struct S2
    {
        public S3 Field2;
    }

    public ref struct S3
    {
        private S1 field;

        public S1 this[S1 i]
        {
            get
            {
                return i;
            }
            set
            {
                field = i;
            }
        }

        public S1 Field2;
    }

    public ref struct S1
    {
        public static implicit operator S1(Span<int> o) => default;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (15,38): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         return new S2() { Field2 = {[inner] = outer} };
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(15, 38),
                // (25,16): error CS8352: Cannot use variable 'x' in this context because it may expose referenced variables outside of their declaration scope
                //         return x;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "x").WithArguments("x").WithLocation(25, 16),
                // (33,37): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //         return new S2() { Field2 = {[outer] = inner} };
                Diagnostic(ErrorCode.ERR_EscapeVariable, "[outer] = inner").WithArguments("inner").WithLocation(33, 37),
                // (67,19): warning CS0649: Field 'Program.S3.Field2' is never assigned to, and will always have its default value 
                //         public S1 Field2;
                Diagnostic(ErrorCode.WRN_UnassignedInternalField, "Field2").WithArguments("Program.S3.Field2", "").WithLocation(67, 19)
                );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeColInitializer(LanguageVersion languageVersion)
        {
            var text = @"
using System;
using System.Collections.Generic;

// X cannot be a ref-like type since it must implement IEnumerable
// that significantly reduces the number of scenarios that could be applicable to ref-like types
class X : List<int>
{
    void Add(Span<int> x, int y) { }

    static void Main()
    {
        Span<int> inner = stackalloc int[1];

        var z = new X { { inner, 12 } };
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics();
        }

        [Fact]
        public void RefLikeEscapeMixingDelegate()
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
        }

        void Test1()
        {
            S1 rOuter = default;

            Span<int> inner = stackalloc int[2];
            S1 rInner = MayWrap(ref inner);

            // valid
            var dummy1 = new Program(ref rOuter, ref rOuter);

            // error
            var dummy2 = new Program(ref rOuter, ref rInner);

            // error
            var dummy3 = new Program(ref inner, ref rOuter);
        }

        Program(ref Span<int> arg1, ref S1 arg2)
        {
            arg2 = MayWrap(ref arg1);
        }

        Program(ref S1 arg1, ref S1 arg2)
        {
            arg1 = arg2;
        }

        static S1 MayWrap(ref Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10);
            comp.VerifyDiagnostics(
                // (20,54): error CS8526: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //             var dummy2 = new Program(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(20, 54),
                // (20,26): error CS8524: This combination of arguments to 'Program.Program(ref Program.S1, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg2' outside of their declaration scope
                //             var dummy2 = new Program(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "new Program(ref rOuter, ref rInner)").WithArguments("Program.Program(ref Program.S1, ref Program.S1)", "arg2").WithLocation(20, 26),
                // (23,42): error CS8526: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             var dummy3 = new Program(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(23, 42),
                // (23,26): error CS8524: This combination of arguments to 'Program.Program(ref Span<int>, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //             var dummy3 = new Program(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "new Program(ref inner, ref rOuter)").WithArguments("Program.Program(ref System.Span<int>, ref Program.S1)", "arg1").WithLocation(23, 26)
            );

            comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular11);
            comp.VerifyDiagnostics(
                // (20,54): error CS8352: Cannot use variable 'rInner' in this context because it may expose referenced variables outside of their declaration scope
                //             var dummy2 = new Program(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "rInner").WithArguments("rInner").WithLocation(20, 54),
                // (20,26): error CS8350: This combination of arguments to 'Program.Program(ref Program.S1, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg2' outside of their declaration scope
                //             var dummy2 = new Program(ref rOuter, ref rInner);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "new Program(ref rOuter, ref rInner)").WithArguments("Program.Program(ref Program.S1, ref Program.S1)", "arg2").WithLocation(20, 26),
                // (23,42): error CS8352: Cannot use variable 'inner' in this context because it may expose referenced variables outside of their declaration scope
                //             var dummy3 = new Program(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "inner").WithArguments("inner").WithLocation(23, 42),
                // (23,26): error CS8350: This combination of arguments to 'Program.Program(ref Span<int>, ref Program.S1)' is disallowed because it may expose variables referenced by parameter 'arg1' outside of their declaration scope
                //             var dummy3 = new Program(ref inner, ref rOuter);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "new Program(ref inner, ref rOuter)").WithArguments("Program.Program(ref System.Span<int>, ref Program.S1)", "arg1").WithLocation(23, 26),
                // (28,32): error CS9077: Cannot return a parameter by reference 'arg1' through a ref parameter; it can only be returned in a return statement
                //             arg2 = MayWrap(ref arg1);
                Diagnostic(ErrorCode.ERR_RefReturnOnlyParameter, "arg1").WithArguments("arg1").WithLocation(28, 32),
                // (28,20): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             arg2 = MayWrap(ref arg1);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref arg1)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(28, 20)
            );
        }

        [Fact]
        public void RefLikeEscapeMixingCallOptionalIn()
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {
        }

        void Test1()
        {
            S1 rOuter = default;

            Span<int> inner = stackalloc int[1];
            S1 rInner = MayWrap(inner);

            // valid, optional arg is of the same escape level
            MayAssign(ref rOuter);

            // valid, optional arg is of wider escape level
            MayAssign(ref rInner);
        }

        static void MayAssign(ref S1 arg1, in Span<int> arg2 = default)
        {
            arg1 = MayWrap(arg2);
        }

        static S1 MayWrap(in Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular10).VerifyDiagnostics();

            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular11).VerifyDiagnostics(
                // (25,20): error CS8347: Cannot use a result of 'Program.MayWrap(in Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             arg1 = MayWrap(arg2);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(arg2)").WithArguments("Program.MayWrap(in System.Span<int>)", "arg").WithLocation(25, 20),
                // (25,28): error CS9077: Cannot return a parameter by reference 'arg2' through a ref parameter; it can only be returned in a return statement
                //             arg1 = MayWrap(arg2);
                Diagnostic(ErrorCode.ERR_RefReturnOnlyParameter, "arg2").WithArguments("arg2").WithLocation(25, 28));
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void MismatchedRefTernaryEscape(LanguageVersion languageVersion)
        {
            var text = @"
class Program
{
    static void Main()
    {
    }

    public static int field = 1;

    bool flag = true;

    ref int Test1()
    {
        var local = 42;

        if (flag) 
            return ref true ? ref field : ref local;

        ref var lr = ref local;
        ref var fr = ref field;

        ref var ternary1 = ref true ? ref lr : ref fr;

        if (flag) 
            return ref ternary1;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (17,47): error CS8168: Cannot return local 'local' by reference because it is not a ref local
                //             return ref true ? ref field : ref local;
                Diagnostic(ErrorCode.ERR_RefReturnLocal, "local").WithArguments("local").WithLocation(17, 47),
                // (25,24): error CS8157: Cannot return 'ternary1' by reference because it was initialized to a value that cannot be returned by reference
                //             return ref ternary1;
                Diagnostic(ErrorCode.ERR_RefReturnNonreturnableLocal, "ternary1").WithArguments("ternary1").WithLocation(25, 24),
                // (12,13): error CS0161: 'Program.Test1()': not all code paths return a value
                //     ref int Test1()
                Diagnostic(ErrorCode.ERR_ReturnExpected, "Test1").WithArguments("Program.Test1()").WithLocation(12, 13)
                );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void MismatchedRefTernaryEscapeBlock(LanguageVersion languageVersion)
        {
            var text = @"
using System;
class Program
{
    static void Main()
    {
    }

    public static int field = 1;

    void Test1()
    {
        Span<int> outer = default;
        var sOuter = MayWrap(ref outer);

        {
            Span<int> inner = stackalloc int[1];
            var sInner = MayWrap(ref inner);

            ref var ternarySame1 = ref true ? ref sInner : ref sInner;
            ref var ternarySame2 = ref true ? ref sOuter : ref sOuter;

            // ok
            ternarySame2 = true ? sOuter : sOuter;

            // error
            ternarySame2 = true ? sOuter : sInner;

            // error
            ternarySame2 = true ? sInner : sOuter;

            // error, mixing val escapes
            ref var ternary1 = ref true ? ref sOuter : ref sInner;

            // error, mixing val escapes
            ref var ternary2 = ref true ? ref sInner : ref sOuter;

            // error, mixing val escapes
            ref var ternary3 = ref true ? ref ternarySame1 : ref ternarySame2;

            ref var ir = ref sInner[1];
            ref var or = ref sOuter[1];

            // no error, indexer cannot ref-return the instance, so ir and or are both safe to return
            ref var ternary4 = ref true ? ref ir : ref or;
        }
    }

    static S1 MayWrap(ref Span<int> arg)
    {
        return default;
    }

    ref struct S1
    {
        public ref int this[int i] => throw null;
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyDiagnostics(
                // (27,44): error CS8526: Cannot use variable 'sInner' in this context because it may expose referenced variables outside of their declaration scope
                //             ternarySame2 = true ? sOuter : sInner;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(27, 44),
                // (30,35): error CS8526: Cannot use variable 'sInner' in this context because it may expose referenced variables outside of their declaration scope
                //             ternarySame2 = true ? sInner : sOuter;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(30, 35),
                // (33,60): error CS8526: Cannot use variable 'sInner' in this context because it may expose referenced variables outside of their declaration scope
                //             ref var ternary1 = ref true ? ref sOuter : ref sInner;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(33, 60),
                // (33,36): error CS8525: Branches of a ref ternary operator cannot refer to variables with incompatible declaration scopes
                //             ref var ternary1 = ref true ? ref sOuter : ref sInner;
                Diagnostic(ErrorCode.ERR_MismatchedRefEscapeInTernary, "true ? ref sOuter : ref sInner").WithLocation(33, 36),
                // (36,47): error CS8526: Cannot use variable 'sInner' in this context because it may expose referenced variables outside of their declaration scope
                //             ref var ternary2 = ref true ? ref sInner : ref sOuter;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(36, 47),
                // (36,36): error CS8525: Branches of a ref ternary operator cannot refer to variables with incompatible declaration scopes
                //             ref var ternary2 = ref true ? ref sInner : ref sOuter;
                Diagnostic(ErrorCode.ERR_MismatchedRefEscapeInTernary, "true ? ref sInner : ref sOuter").WithLocation(36, 36),
                // (39,47): error CS8526: Cannot use variable 'ternarySame1' in this context because it may expose referenced variables outside of their declaration scope
                //             ref var ternary3 = ref true ? ref ternarySame1 : ref ternarySame2;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "ternarySame1").WithArguments("ternarySame1").WithLocation(39, 47),
                // (39,36): error CS8525: Branches of a ref ternary operator cannot refer to variables with incompatible declaration scopes
                //             ref var ternary3 = ref true ? ref ternarySame1 : ref ternarySame2;
                Diagnostic(ErrorCode.ERR_MismatchedRefEscapeInTernary, "true ? ref ternarySame1 : ref ternarySame2").WithLocation(39, 36)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void MismatchedRefTernaryEscapeBlock_UnsafeContext(LanguageVersion languageVersion)
        {
            var text = @"
using System;
unsafe class Program
{
    static void Main()
    {
    }

    public static int field = 1;

    void Test1()
    {
        Span<int> outer = default;
        var sOuter = MayWrap(ref outer);

        {
            Span<int> inner = stackalloc int[1];
            var sInner = MayWrap(ref inner);

            ref var ternarySame1 = ref true ? ref sInner : ref sInner;
            ref var ternarySame2 = ref true ? ref sOuter : ref sOuter;

            // ok
            ternarySame2 = true ? sOuter : sOuter;

            // error
            ternarySame2 = true ? sOuter : sInner;

            // error
            ternarySame2 = true ? sInner : sOuter;

            // error, mixing val escapes
            ref var ternary1 = ref true ? ref sOuter : ref sInner;

            // error, mixing val escapes
            ref var ternary2 = ref true ? ref sInner : ref sOuter;

            // error, mixing val escapes
            ref var ternary3 = ref true ? ref ternarySame1 : ref ternarySame2;

            ref var ir = ref sInner[1];
            ref var or = ref sOuter[1];

            // no error, indexer cannot ref-return the instance, so ir and or are both safe to return
            ref var ternary4 = ref true ? ref ir : ref or;
        }
    }

    static S1 MayWrap(ref Span<int> arg)
    {
        return default;
    }

    ref struct S1
    {
        public ref int this[int i] => throw null;
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, options: TestOptions.UnsafeDebugDll, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyEmitDiagnostics(
                // (27,44): warning CS9077: Use of variable 'sInner' in this context may expose referenced variables outside of their declaration scope
                //             ternarySame2 = true ? sOuter : sInner;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(27, 44),
                // (30,35): warning CS9077: Use of variable 'sInner' in this context may expose referenced variables outside of their declaration scope
                //             ternarySame2 = true ? sInner : sOuter;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(30, 35),
                // (33,60): warning CS9077: Use of variable 'sInner' in this context may expose referenced variables outside of their declaration scope
                //             ref var ternary1 = ref true ? ref sOuter : ref sInner;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(33, 60),
                // (33,36): warning CS9083: The branches of the ref conditional operator refer to variables with incompatible declaration scopes
                //             ref var ternary1 = ref true ? ref sOuter : ref sInner;
                Diagnostic(ErrorCode.WRN_MismatchedRefEscapeInTernary, "true ? ref sOuter : ref sInner").WithLocation(33, 36),
                // (36,47): warning CS9077: Use of variable 'sInner' in this context may expose referenced variables outside of their declaration scope
                //             ref var ternary2 = ref true ? ref sInner : ref sOuter;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "sInner").WithArguments("sInner").WithLocation(36, 47),
                // (36,36): warning CS9083: The branches of the ref conditional operator refer to variables with incompatible declaration scopes
                //             ref var ternary2 = ref true ? ref sInner : ref sOuter;
                Diagnostic(ErrorCode.WRN_MismatchedRefEscapeInTernary, "true ? ref sInner : ref sOuter").WithLocation(36, 36),
                // (39,47): warning CS9077: Use of variable 'ternarySame1' in this context may expose referenced variables outside of their declaration scope
                //             ref var ternary3 = ref true ? ref ternarySame1 : ref ternarySame2;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "ternarySame1").WithArguments("ternarySame1").WithLocation(39, 47),
                // (39,36): warning CS9083: The branches of the ref conditional operator refer to variables with incompatible declaration scopes
                //             ref var ternary3 = ref true ? ref ternarySame1 : ref ternarySame2;
                Diagnostic(ErrorCode.WRN_MismatchedRefEscapeInTernary, "true ? ref ternarySame1 : ref ternarySame2").WithLocation(39, 36)
                );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void StackallocEscape(LanguageVersion languageVersion)
        {
            var text = @"
    using System;
    class Program
    {
        static void Main()
        {

        }

        Span<int> Test0()
        {
            // valid, baseline
            return default(Span<int>);
        }

        Span<int> Test3()
        {
            Span<int> local = stackalloc int[10];
            return true? local : default(Span<int>);
        }
      
        Span<int> Test4(Span<int> arg)
        {
            arg = stackalloc int[10];
            return arg;
        }

        Span<int> Test6()
        {
            Span<int> local = default;
            local = stackalloc int[10];
            return local;
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (19,26): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope 
                //             return true? local : default(Span<int>);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(19, 26),
                // (24,19): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //             arg = stackalloc int[10];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[10]").WithArguments("System.Span<int>").WithLocation(24, 19),
                // (31,21): error CS8353: A result of a stackalloc expression of type 'Span<int>' cannot be used in this context because it may be exposed outside of the containing method
                //             local = stackalloc int[10];
                Diagnostic(ErrorCode.ERR_EscapeStackAlloc, "stackalloc int[10]").WithArguments("System.Span<int>").WithLocation(31, 21)
                );
        }

        [WorkItem(21831, "https://github.com/dotnet/roslyn/issues/21831")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void LocalWithNoInitializerEscape(LanguageVersion languageVersion)
        {
            var text = @"using System;

    class Program
    {
        static void Main()
        {
            // uninitialized
            S1 sp;

            // ok
            sp = default;
            
            Span<int> local = stackalloc int[1];

            // error
            sp = MayWrap(ref local);
        }

        static S1 SefReferringTest()
        {
            S1 sp1 = sp1;
            return sp1;
        }

        static S1 MayWrap(ref Span<int> arg)
        {
            return default;
        }

        ref struct S1
        {
            public ref int this[int i] => throw null;
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyDiagnostics(
                // (16,30): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //             sp = MayWrap(ref local);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(16, 30),
                // (16,18): error CS8347: Cannot use a result of 'Program.MayWrap(ref Span<int>)' in this context because it may expose variables referenced by parameter 'arg' outside of their declaration scope
                //             sp = MayWrap(ref local);
                Diagnostic(ErrorCode.ERR_EscapeCall, "MayWrap(ref local)").WithArguments("Program.MayWrap(ref System.Span<int>)", "arg").WithLocation(16, 18),
                // (22,20): error CS8352: Cannot use variable 'sp1' in this context because it may expose referenced variables outside of their declaration scope
                //             return sp1;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "sp1").WithArguments("sp1").WithLocation(22, 20)
                );
        }

        [WorkItem(21858, "https://github.com/dotnet/roslyn/issues/21858")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void FieldOfRefLikeEscape(LanguageVersion languageVersion)
        {
            var text = @"
    class Program
    {
        static void Main()
        {
        }

        ref struct S1
        {
            private S2 x;

            public S1(S2 arg) => x = arg;

            public S2 M1()
            {
                // ok
                return x;
            }

            public S2 M2()
            {
                var toReturn = x;

                // ok
                return toReturn;
            }

            public ref S2 M3()
            {
                // not ok
                return ref x;
            }
        }

        ref struct S2{}

    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (31,28): error CS8170: Struct members cannot return 'this' or other instance members by reference
                //                 return ref x;
                Diagnostic(ErrorCode.ERR_RefReturnStructThis, "x").WithLocation(31, 28)
                );
        }

        [WorkItem(21880, "https://github.com/dotnet/roslyn/issues/21880")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void MemberOfReadonlyRefLikeEscape(LanguageVersion languageVersion)
        {
            var text = @"
    using System;
    using System.Diagnostics.CodeAnalysis;
    public static class Program
    {
        public static void Main()
        {
            // OK, SR is readonly
            Span<int> value1 = stackalloc int[1];
            new SR().TryGet(out value1);

            // Ok, the new value can be copied into SW but not the 
            // ref to the value
            new SW().TryGet(out value1);

            // Error as the ref of this can escape into value2
            Span<int> value2 = default;
            new SW().TryGet2(out value2);
        }
    }

    public readonly ref struct SR
    {
        public void TryGet(out Span<int> result)
        {
            result = default;
        }
    }

    public ref struct SW
    {
        public void TryGet(out Span<int> result)
        {
            result = default;
        }

        [UnscopedRef]
        public void TryGet2(out Span<int> result)
        {
            result = default;
        }
    }
";
            if (languageVersion == LanguageVersion.CSharp10)
            {
                CreateCompilationWithMscorlibAndSpan(new[] { text, UnscopedRefAttributeDefinition }, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                    // (14,13): error CS8350: This combination of arguments to 'SW.TryGet(out Span<int>)' is disallowed because it may expose variables referenced by parameter 'result' outside of their declaration scope
                    //             new SW().TryGet(out value1);
                    Diagnostic(ErrorCode.ERR_CallArgMixing, "new SW().TryGet(out value1)").WithArguments("SW.TryGet(out System.Span<int>)", "result").WithLocation(14, 13),
                    // (14,33): error CS8352: Cannot use variable 'value1' in this context because it may expose referenced variables outside of their declaration scope
                    //             new SW().TryGet(out value1);
                    Diagnostic(ErrorCode.ERR_EscapeVariable, "value1").WithArguments("value1").WithLocation(14, 33)
                    );

            }
            else
            {
                CreateCompilationWithMscorlibAndSpan(new[] { text, UnscopedRefAttributeDefinition }, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                    // (18,13): error CS8156: An expression cannot be used in this context because it may not be passed or returned by reference
                    //             new SW().TryGet2(out value2);
                    Diagnostic(ErrorCode.ERR_RefReturnLvalueExpected, "new SW()").WithLocation(18, 13),
                    // (18,13): error CS8350: This combination of arguments to 'SW.TryGet2(out Span<int>)' is disallowed because it may expose variables referenced by parameter 'this' outside of their declaration scope
                    //             new SW().TryGet2(out value2);
                    Diagnostic(ErrorCode.ERR_CallArgMixing, "new SW().TryGet2(out value2)").WithArguments("SW.TryGet2(out System.Span<int>)", "this").WithLocation(18, 13)
                    );
            }
        }

        [WorkItem(21911, "https://github.com/dotnet/roslyn/issues/21911")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void MemberOfReadonlyRefLikeEscapeSpans(LanguageVersion languageVersion)
        {
            var text = @"
    using System;

    public static class Program
    {
        public static void Main()
        {
            Span<int> stackAllocated = stackalloc int[100];

            // OK, Span is a readonly struct
            new Span<int>().CopyTo(stackAllocated);

            // OK, ReadOnlySpan is a readonly struct
            new ReadOnlySpan<int>().CopyTo(stackAllocated);

            // not OK, Span is writeable
            new NotReadOnly<int>().CopyTo(stackAllocated);
        }
    }

    public ref struct NotReadOnly<T>
    {
        private Span<T> wrapped;

        public void CopyTo(Span<T> other)
        {
            wrapped = other;
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (17,43): error CS8352: Cannot use variable 'stackAllocated' in this context because it may expose referenced variables outside of their declaration scope
                //             new NotReadOnly<int>().CopyTo(stackAllocated);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "stackAllocated").WithArguments("stackAllocated").WithLocation(17, 43),
                // (17,13): error CS8350: This combination of arguments to 'NotReadOnly<int>.CopyTo(Span<int>)' is disallowed because it may expose variables referenced by parameter 'other' outside of their declaration scope
                //             new NotReadOnly<int>().CopyTo(stackAllocated);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "new NotReadOnly<int>().CopyTo(stackAllocated)").WithArguments("NotReadOnly<int>.CopyTo(System.Span<int>)", "other").WithLocation(17, 13)
                );
        }

        [WorkItem(35146, "https://github.com/dotnet/roslyn/issues/35146")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ReadOnlyRefStruct_Method_RefLikeStructParameter(LanguageVersion languageVersion)
        {
            var csharp = @"
using System;

public readonly ref struct S<T>
{
    public void M(Span<T> x) { }

    public unsafe static void N(S<byte> b)
    {
        Span<byte> x = stackalloc byte[5];
        b.M(x);
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(csharp, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics();
        }

        [WorkItem(35146, "https://github.com/dotnet/roslyn/issues/35146")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ReadOnlyMethod_RefLikeStructParameter(LanguageVersion languageVersion)
        {
            var csharp = @"
using System;

public ref struct S<T>
{
    public readonly void M(Span<T> x) { }

    public unsafe static void N(S<byte> b)
    {
        Span<byte> x = stackalloc byte[5];
        b.M(x);
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(csharp, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics();
        }

        [WorkItem(35146, "https://github.com/dotnet/roslyn/issues/35146")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ReadOnlyRefStruct_RefLikeProperty(LanguageVersion languageVersion)
        {
            var csharp = @"
using System;

public readonly ref struct S<T>
{
    public Span<T> P { get => default; set {} }

    public unsafe static void N(S<byte> b)
    {
        Span<byte> x = stackalloc byte[5];
        b.P = x;
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(csharp, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics(
                // (11,15): warning CS9080: Use of variable 'x' in this context may expose referenced variables outside of their declaration scope
                //         b.P = x;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "x").WithArguments("x").WithLocation(11, 15));
            var verifier = CompileAndVerify(comp, verify: Verification.Skipped);
            verifier.VerifyIL("S<T>.N", @"
{
  // Code size       24 (0x18)
  .maxstack  2
  .locals init (System.Span<byte> V_0, //x
                System.Span<byte> V_1)
  IL_0000:  nop
  IL_0001:  ldc.i4.5
  IL_0002:  conv.u
  IL_0003:  localloc
  IL_0005:  ldc.i4.5
  IL_0006:  newobj     ""System.Span<byte>..ctor(void*, int)""
  IL_000b:  stloc.1
  IL_000c:  ldloc.1
  IL_000d:  stloc.0
  IL_000e:  ldarga.s   V_0
  IL_0010:  ldloc.0
  IL_0011:  call       ""void S<byte>.P.set""
  IL_0016:  nop
  IL_0017:  ret
}
");
        }

        [WorkItem(35146, "https://github.com/dotnet/roslyn/issues/35146")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ReadOnlyRefLikeProperty_01(LanguageVersion languageVersion)
        {
            var csharp = @"
using System;

public ref struct S<T>
{
    public readonly Span<T> P { get => default; set {} }

    public unsafe static void N(S<byte> b)
    {
        Span<byte> x = stackalloc byte[5];
        b.P = x;
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(csharp, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics(
                // (11,15): warning CS9080: Use of variable 'x' in this context may expose referenced variables outside of their declaration scope
                //         b.P = x;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "x").WithArguments("x").WithLocation(11, 15));
            var verifier = CompileAndVerify(comp, verify: Verification.Skipped);
            verifier.VerifyIL("S<T>.N", @"
{
  // Code size       24 (0x18)
  .maxstack  2
  .locals init (System.Span<byte> V_0, //x
                System.Span<byte> V_1)
  IL_0000:  nop
  IL_0001:  ldc.i4.5
  IL_0002:  conv.u
  IL_0003:  localloc
  IL_0005:  ldc.i4.5
  IL_0006:  newobj     ""System.Span<byte>..ctor(void*, int)""
  IL_000b:  stloc.1
  IL_000c:  ldloc.1
  IL_000d:  stloc.0
  IL_000e:  ldarga.s   V_0
  IL_0010:  ldloc.0
  IL_0011:  call       ""readonly void S<byte>.P.set""
  IL_0016:  nop
  IL_0017:  ret
}
");
        }

        [WorkItem(35146, "https://github.com/dotnet/roslyn/issues/35146")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ReadOnlyRefLikeProperty_02(LanguageVersion languageVersion)
        {
            var csharp = @"
using System;

public ref struct S<T>
{
    public Span<T> P { get => default; readonly set {} }

    public unsafe static void N(S<byte> b)
    {
        Span<byte> x = stackalloc byte[5];
        b.P = x;
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(csharp, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics(
                // (11,15): warning CS9080: Use of variable 'x' in this context may expose referenced variables outside of their declaration scope
                //         b.P = x;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "x").WithArguments("x").WithLocation(11, 15));
            var verifier = CompileAndVerify(comp, verify: Verification.Skipped);
            verifier.VerifyIL("S<T>.N", @"
{
  // Code size       24 (0x18)
  .maxstack  2
  .locals init (System.Span<byte> V_0, //x
                System.Span<byte> V_1)
  IL_0000:  nop
  IL_0001:  ldc.i4.5
  IL_0002:  conv.u
  IL_0003:  localloc
  IL_0005:  ldc.i4.5
  IL_0006:  newobj     ""System.Span<byte>..ctor(void*, int)""
  IL_000b:  stloc.1
  IL_000c:  ldloc.1
  IL_000d:  stloc.0
  IL_000e:  ldarga.s   V_0
  IL_0010:  ldloc.0
  IL_0011:  call       ""readonly void S<byte>.P.set""
  IL_0016:  nop
  IL_0017:  ret
}
");
        }

        [WorkItem(35146, "https://github.com/dotnet/roslyn/issues/35146")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ReadOnlyIndexer_RefLikeStructParameter_01(LanguageVersion languageVersion)
        {
            var csharp = @"
using System;

public ref struct S<T>
{
    public readonly Span<T> this[Span<T> span] { get => default; set {} }

    public unsafe static Span<byte> N(S<byte> b)
    {
        Span<byte> x = stackalloc byte[5];
        _ = b[x];
        b[x] = x;
        return b[x];
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(csharp, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics(
                // (13,18): warning CS9080: Use of variable 'x' in this context may expose referenced variables outside of their declaration scope
                //         return b[x];
                Diagnostic(ErrorCode.WRN_EscapeVariable, "x").WithArguments("x").WithLocation(13, 18));
        }

        [WorkItem(35146, "https://github.com/dotnet/roslyn/issues/35146")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ReadOnlyIndexer_RefLikeStructParameter_02(LanguageVersion languageVersion)
        {
            var csharp = @"
using System;
public ref struct S<T>
{
    public Span<T> this[Span<T> span] { get => default; readonly set {} }

    public unsafe static void N(S<byte> b)
    {
        Span<byte> x = stackalloc byte[5];
        _ = b[x];
        b[x] = x;
    }
}
";
            var comp = CreateCompilationWithMscorlibAndSpan(csharp, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeDebugDll);
            comp.VerifyDiagnostics(
                // (10,15): warning CS9080: Use of variable 'x' in this context may expose referenced variables outside of their declaration scope
                //         _ = b[x];
                Diagnostic(ErrorCode.WRN_EscapeVariable, "x").WithArguments("x").WithLocation(10, 15),
                // (11,11): warning CS9080: Use of variable 'x' in this context may expose referenced variables outside of their declaration scope
                //         b[x] = x;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "x").WithArguments("x").WithLocation(11, 11));
            var verifier = CompileAndVerify(comp, verify: Verification.Skipped);
            verifier.VerifyIL("S<T>.N", @"
{
  // Code size       34 (0x22)
  .maxstack  3
  .locals init (System.Span<byte> V_0, //x
                System.Span<byte> V_1)
  IL_0000:  nop
  IL_0001:  ldc.i4.5
  IL_0002:  conv.u
  IL_0003:  localloc
  IL_0005:  ldc.i4.5
  IL_0006:  newobj     ""System.Span<byte>..ctor(void*, int)""
  IL_000b:  stloc.1
  IL_000c:  ldloc.1
  IL_000d:  stloc.0
  IL_000e:  ldarga.s   V_0
  IL_0010:  ldloc.0
  IL_0011:  call       ""System.Span<byte> S<byte>.this[System.Span<byte>].get""
  IL_0016:  pop
  IL_0017:  ldarga.s   V_0
  IL_0019:  ldloc.0
  IL_001a:  ldloc.0
  IL_001b:  call       ""readonly void S<byte>.this[System.Span<byte>].set""
  IL_0020:  nop
  IL_0021:  ret
}
");
        }

        [WorkItem(22197, "https://github.com/dotnet/roslyn/issues/22197")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefTernaryMustMatchValEscapes(LanguageVersion languageVersion)
        {
            var text = @"
    using System;

    public class C
    {
        bool flag1 = true;
        bool flag2 = false;

        public void M(ref Span<int> global)
        {
            Span<int> local = stackalloc int[10];

            ref var r1 = ref (flag1 ? ref global : ref local);
            ref var r2 = ref (flag2 ? ref global : ref local);

	        // same as         global = local;   which would be an error.
            // but we can’t fail here, since r1 and r2 are basically the same, 
            // so should fail when making r1 and r2 above.
            r1 = r2;   
        }

        public static void Main()
        {
        }
    }
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (13,56): error CS8526: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //             ref var r1 = ref (flag1 ? ref global : ref local);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(13, 56),
                // (13,31): error CS8525: Branches of a ref ternary operator cannot refer to variables with incompatible declaration scopes
                //             ref var r1 = ref (flag1 ? ref global : ref local);
                Diagnostic(ErrorCode.ERR_MismatchedRefEscapeInTernary, "flag1 ? ref global : ref local").WithLocation(13, 31),
                // (14,56): error CS8526: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //             ref var r2 = ref (flag2 ? ref global : ref local);
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(14, 56),
                // (14,31): error CS8525: Branches of a ref ternary operator cannot refer to variables with incompatible declaration scopes
                //             ref var r2 = ref (flag2 ? ref global : ref local);
                Diagnostic(ErrorCode.ERR_MismatchedRefEscapeInTernary, "flag2 ? ref global : ref local").WithLocation(14, 31)
            );
        }

        [WorkItem(22197, "https://github.com/dotnet/roslyn/issues/22197")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefTernaryMustMatchValEscapes1(LanguageVersion languageVersion)
        {
            var text = @"
    using System;

    public class C
    {
        public void M(ref Span<int> global)
        {
            Span<int> local = stackalloc int[0];

            // ok
            (true ? ref local : ref local) = (false ? ref global : ref global);

            // also OK
            (true ? ref local : ref local) = (false ? global : local);
        }
    }
";
            var comp = CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion));
            comp.VerifyDiagnostics();

            var compiled = CompileAndVerify(comp, verify: Verification.Passes);
            compiled.VerifyIL("C.M(ref System.Span<int>)", @"
{
  // Code size        8 (0x8)
  .maxstack  1
  IL_0000:  ldarg.1
  IL_0001:  ldobj      ""System.Span<int>""
  IL_0006:  pop
  IL_0007:  ret
}
");
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void DeconstructionAssignmentToGlobal(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        Span<int> local = stackalloc int[10];
        (global, global) = global;
        (global, global) = local; // error 1
        (global, local) = local; // error 2
        (local, local) = local;
        (global, _) = local; // error 3
        (local, _) = local;
        (global, _) = global;
    }
    public static void Main()
    {
    }
}
public static class Extensions
{
    public static void Deconstruct(this Span<int> self, out Span<int> x, out Span<int> y)
    {
        throw null;
    }
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (10,28): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (global, global) = local; // error 1
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(10, 28),
                // (11,27): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (global, local) = local; // error 2
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(11, 27),
                // (13,23): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (global, _) = local; // error 3
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(13, 23)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void DeconstructionAssignmentToRefMethods(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        Span<int> local = stackalloc int[10];
        (M(), M()) = local; // error
    }
    public static void Main() => throw null;
    public ref Span<int> M() => throw null;
}
public static class Extensions
{
    public static void Deconstruct(this Span<int> self, out Span<int> x, out Span<int> y) => throw null;
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (9,22): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (M(), M()) = local; // error
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(9, 22)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void DeconstructionAssignmentWithRefExtension(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        (global, global) = global;
    }
    public static void Main() => throw null;
}
public static class Extensions
{
    public static void Deconstruct(ref this Span<int> self, out Span<int> x, out Span<int> y) => throw null;
}
";
            if (languageVersion == LanguageVersion.CSharp10)
            {
                CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                    // (8,9): error CS1510: A ref or out value must be an assignable variable
                    //         (global, global) = global;
                    Diagnostic(ErrorCode.ERR_RefLvalueExpected, "(global, global) = global").WithLocation(8, 9),
                    // (8,9): error CS8352: Cannot use variable '(global, global) = global' in this context because it may expose referenced variables outside of their declaration scope
                    //         (global, global) = global;
                    Diagnostic(ErrorCode.ERR_EscapeVariable, "(global, global) = global").WithArguments("(global, global) = global").WithLocation(8, 9),
                    // (8,28): error CS8350: This combination of arguments to 'Extensions.Deconstruct(ref Span<int>, out Span<int>, out Span<int>)' is disallowed because it may expose variables referenced by parameter 'x' outside of their declaration scope
                    //         (global, global) = global;
                    Diagnostic(ErrorCode.ERR_CallArgMixing, "global").WithArguments("Extensions.Deconstruct(ref System.Span<int>, out System.Span<int>, out System.Span<int>)", "x").WithLocation(8, 28)
                );
            }
            else
            {
                CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                    // (8,9): error CS1510: A ref or out value must be an assignable variable
                    //         (global, global) = global;
                    Diagnostic(ErrorCode.ERR_RefLvalueExpected, "(global, global) = global").WithLocation(8, 9)
                );
            }
        }

        [Fact]
        public void DeconstructionAssignmentWithRefExtension_UnsafeContext()
        {
            var text = @"
using System;

public unsafe class C
{
    public void M(ref Span<int> global)
    {
        (global, global) = global;
    }
    public static void Main() => throw null;
}
public static class Extensions
{
    public static void Deconstruct(ref this Span<int> self, out Span<int> x, out Span<int> y) => throw null;
}
";
            CreateCompilationWithMscorlibAndSpan(text, options: TestOptions.UnsafeDebugDll, parseOptions: TestOptions.Regular10).VerifyDiagnostics(
                // (8,9): error CS1510: A ref or out value must be an assignable variable
                //         (global, global) = global;
                Diagnostic(ErrorCode.ERR_RefLvalueExpected, "(global, global) = global").WithLocation(8, 9),
                // (8,9): warning CS9077: Use of variable '(global, global) = global' in this context may expose referenced variables outside of their declaration scope
                //         (global, global) = global;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "(global, global) = global").WithArguments("(global, global) = global").WithLocation(8, 9),
                // (8,9): warning CS9077: Use of variable '(global, global) = global' in this context may expose referenced variables outside of their declaration scope
                //         (global, global) = global;
                Diagnostic(ErrorCode.WRN_EscapeVariable, "(global, global) = global").WithArguments("(global, global) = global").WithLocation(8, 9)
                );

            CreateCompilationWithMscorlibAndSpan(text, options: TestOptions.UnsafeDebugDll, parseOptions: TestOptions.Regular11).VerifyDiagnostics(
                // (8,9): error CS1510: A ref or out value must be an assignable variable
                //         (global, global) = global;
                Diagnostic(ErrorCode.ERR_RefLvalueExpected, "(global, global) = global").WithLocation(8, 9)
                );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void DeconstructionAssignmentWithRefReadonlyExtension(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        Span<int> local = stackalloc int[10];
        (global, global) = global;
        (global, global) = local; // error
    }
    public static void Main() => throw null;
}
public static class Extensions
{
    public static void Deconstruct(in this Span<int> self, out Span<int> x, out Span<int> y) => throw null;
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (10,28): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (global, global) = local; // error
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(10, 28)
            );
        }

        [Fact]
        public void DeconstructionAssignmentWithReturnValue()
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        var t = ((global, global) = global); // error
    }
    public static void Main() => throw null;
}
public static class Extensions
{
    public static void Deconstruct(this Span<int> self, out Span<int> x, out Span<int> y) => throw null;
}
namespace System
{
    public struct ValueTuple<T1, T2>
    {
        public T1 Item1;
        public T2 Item2;

        public ValueTuple(T1 item1, T2 item2)
        {
            this.Item1 = item1;
            this.Item2 = item2;
        }
    }
}";
            CreateCompilationWithMscorlibAndSpan(text).VerifyDiagnostics(
                // (8,19): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         var t = ((global, global) = global); // error
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "global").WithArguments("System.Span<int>").WithLocation(8, 19),
                // (8,27): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         var t = ((global, global) = global); // error
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "global").WithArguments("System.Span<int>").WithLocation(8, 27)
            );
        }

        [Fact]
        public void DeconstructionAssignmentOfTuple()
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        Span<int> local = stackalloc int[10];
        string s;
        C c;

        (global, global) = (local, local); // error 1

        (global, s) = (local, """"); // error 2
        (global, s) = (local, null); // error 3

        (local, s) = (global, """"); // error 4
        (local, s) = (global, null); // error 5

        (c, s) = (local, """"); // error 6
        (c, s) = (local, null);
    }
    public static void Main() => throw null;
    public static implicit operator C(Span<int> s) => throw null;
}
namespace System
{
    public struct ValueTuple<T1, T2>
    {
        public T1 Item1;
        public T2 Item2;

        public ValueTuple(T1 item1, T2 item2)
        {
            this.Item1 = item1;
            this.Item2 = item2;
        }
    }
}
";
            var compilation = CreateCompilationWithMscorlibAndSpan(text);
            compilation.VerifyDiagnostics(
                // (12,29): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(12, 29),
                // (12,36): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(12, 36),
                // (14,24): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, s) = (local, ""); // error 2
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(14, 24),
                // (15,24): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, s) = (local, null); // error 3
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(15, 24),
                // (17,23): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (local, s) = (global, ""); // error 4
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "global").WithArguments("System.Span<int>").WithLocation(17, 23),
                // (18,23): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (local, s) = (global, null); // error 5
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "global").WithArguments("System.Span<int>").WithLocation(18, 23),
                // (20,19): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (c, s) = (local, ""); // error 6
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(20, 19)
            );

            // Check the Type and ConvertedType of tuples on the right-hand-side
            var tree = compilation.SyntaxTrees[0];
            var model = compilation.GetSemanticModel(tree);

            var tuple2 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(3);
            Assert.Equal(@"(local, """")", tuple2.ToString());
            Assert.Equal(@"(global, s) = (local, """")", tuple2.Parent.ToString());
            Assert.Equal("(System.Span<int> local, string)", model.GetTypeInfo(tuple2).Type.ToString());
            Assert.Equal("(System.Span<int>, string)", model.GetTypeInfo(tuple2).ConvertedType.ToString());

            var tuple3 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(5);
            Assert.Equal(@"(local, null)", tuple3.ToString());
            Assert.Equal(@"(global, s) = (local, null)", tuple3.Parent.ToString());
            Assert.Null(model.GetTypeInfo(tuple3).Type);
            Assert.Equal("(System.Span<int>, string)", model.GetTypeInfo(tuple3).ConvertedType.ToString());

            var tuple6 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(11);
            Assert.Equal(@"(local, """")", tuple6.ToString());
            Assert.Equal(@"(c, s) = (local, """")", tuple6.Parent.ToString());
            Assert.Equal("(System.Span<int> local, string)", model.GetTypeInfo(tuple6).Type.ToString());
            Assert.Equal("(C, string)", model.GetTypeInfo(tuple6).ConvertedType.ToString());

            var tuple7 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(13);
            Assert.Equal("(local, null)", tuple7.ToString());
            Assert.Equal("(c, s) = (local, null)", tuple7.Parent.ToString());
            Assert.Null(model.GetTypeInfo(tuple7).Type);
            Assert.Equal("(C, string)", model.GetTypeInfo(tuple7).ConvertedType.ToString());
        }

        [Fact]
        public void DeconstructionAssignmentOfTuple_WithoutValueTuple()
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        Span<int> local = stackalloc int[10];
        string s;
        C c;

        (global, global) = (local, local); // error 1

        (global, s) = (local, """"); // error 2
        (global, s) = (local, null); // error 3

        (local, s) = (global, """"); // error 4
        (local, s) = (global, null); // error 5

        (c, s) = (local, """"); // error 6
        (c, s) = (local, null); // error 7
    }
    public static void Main() => throw null;
    public static implicit operator C(Span<int> s) => throw null;
}
";
            var compilation = CreateCompilationWithMscorlibAndSpan(text);
            compilation.VerifyDiagnostics(
                // (12,28): error CS8179: Predefined type 'System.ValueTuple`2' is not defined or imported
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_PredefinedValueTupleTypeNotFound, "(local, local)").WithArguments("System.ValueTuple`2").WithLocation(12, 28),
                // (12,29): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(12, 29),
                // (12,36): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(12, 36),
                // (14,23): error CS8179: Predefined type 'System.ValueTuple`2' is not defined or imported
                //         (global, s) = (local, ""); // error 2
                Diagnostic(ErrorCode.ERR_PredefinedValueTupleTypeNotFound, @"(local, """")").WithArguments("System.ValueTuple`2").WithLocation(14, 23),
                // (14,24): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, s) = (local, ""); // error 2
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(14, 24),
                // (15,23): error CS8179: Predefined type 'System.ValueTuple`2' is not defined or imported
                //         (global, s) = (local, null); // error 3
                Diagnostic(ErrorCode.ERR_PredefinedValueTupleTypeNotFound, "(local, null)").WithArguments("System.ValueTuple`2").WithLocation(15, 23),
                // (17,22): error CS8179: Predefined type 'System.ValueTuple`2' is not defined or imported
                //         (local, s) = (global, ""); // error 4
                Diagnostic(ErrorCode.ERR_PredefinedValueTupleTypeNotFound, @"(global, """")").WithArguments("System.ValueTuple`2").WithLocation(17, 22),
                // (17,23): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (local, s) = (global, ""); // error 4
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "global").WithArguments("System.Span<int>").WithLocation(17, 23),
                // (18,22): error CS8179: Predefined type 'System.ValueTuple`2' is not defined or imported
                //         (local, s) = (global, null); // error 5
                Diagnostic(ErrorCode.ERR_PredefinedValueTupleTypeNotFound, "(global, null)").WithArguments("System.ValueTuple`2").WithLocation(18, 22),
                // (20,18): error CS8179: Predefined type 'System.ValueTuple`2' is not defined or imported
                //         (c, s) = (local, ""); // error 6
                Diagnostic(ErrorCode.ERR_PredefinedValueTupleTypeNotFound, @"(local, """")").WithArguments("System.ValueTuple`2").WithLocation(20, 18),
                // (20,19): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (c, s) = (local, ""); // error 6
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(20, 19),
                // (21,18): error CS8179: Predefined type 'System.ValueTuple`2' is not defined or imported
                //         (c, s) = (local, null); // error 7
                Diagnostic(ErrorCode.ERR_PredefinedValueTupleTypeNotFound, "(local, null)").WithArguments("System.ValueTuple`2").WithLocation(21, 18)
            );

            // Check the Type and ConvertedType of tuples on the right-hand-side
            var tree = compilation.SyntaxTrees[0];
            var model = compilation.GetSemanticModel(tree);

            var tuple2 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(3);
            Assert.Equal(@"(local, """")", tuple2.ToString());
            Assert.Equal(@"(global, s) = (local, """")", tuple2.Parent.ToString());
            Assert.Equal("(System.Span<int> local, string)", model.GetTypeInfo(tuple2).Type.ToString());
            Assert.Equal("(System.Span<int>, string)", model.GetTypeInfo(tuple2).ConvertedType.ToString());

            var tuple3 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(5);
            Assert.Equal(@"(local, null)", tuple3.ToString());
            Assert.Equal(@"(global, s) = (local, null)", tuple3.Parent.ToString());
            Assert.Null(model.GetTypeInfo(tuple3).Type);
            Assert.Equal("(System.Span<int>, string)", model.GetTypeInfo(tuple3).ConvertedType.ToString());

            var tuple6 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(11);
            Assert.Equal(@"(local, """")", tuple6.ToString());
            Assert.Equal(@"(c, s) = (local, """")", tuple6.Parent.ToString());
            Assert.Equal("(System.Span<int> local, string)", model.GetTypeInfo(tuple6).Type.ToString());
            Assert.Equal("(C, string)", model.GetTypeInfo(tuple6).ConvertedType.ToString());

            var tuple7 = tree.GetCompilationUnitRoot().DescendantNodes().OfType<TupleExpressionSyntax>().ElementAt(13);
            Assert.Equal("(local, null)", tuple7.ToString());
            Assert.Equal("(c, s) = (local, null)", tuple7.Parent.ToString());
            Assert.Null(model.GetTypeInfo(tuple7).Type);
            Assert.Equal("(C, string)", model.GetTypeInfo(tuple7).ConvertedType.ToString());
        }

        [Fact]
        public void DeconstructionAssignmentOfRefLikeTuple()
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        Span<int> local = stackalloc int[10];
        String s;
        C c;

        (global, global) = (local, local); // error 1

        (global, s) = (local, """"); // error 2
        (global, s) = (local, null); // error 3

        (local, s) = (global, """"); // error 4
        (local, s) = (global, null); // error 5

        (c, s) = (local, """"); // error 6
        (c, s) = (local, null);
    }
    public static void Main() => throw null;
    public static implicit operator C(Span<int> s) => throw null;
}
namespace System
{
    // Note: there is no way to make a ValueTuple type that will hold Spans and still be recognized as well-known type for tuples
    public ref struct ValueTuple<T1, T2>
    {
        public T1 Item1;
        public T2 Item2;

        public ValueTuple(T1 item1, T2 item2)
        {
            this.Item1 = item1;
            this.Item2 = item2;
        }
    }
}
";
            var compilation = CreateCompilationWithMscorlibAndSpan(text);
            compilation.VerifyDiagnostics(
                // (12,29): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(12, 29),
                // (12,36): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(12, 36),
                // (12,29): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (global, global) = (local, local); // error 1
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(12, 29),
                // (14,24): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, s) = (local, ""); // error 2
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(14, 24),
                // (14,24): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (global, s) = (local, ""); // error 2
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(14, 24),
                // (15,24): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (global, s) = (local, null); // error 3
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(15, 24),
                // (15,24): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //         (global, s) = (local, null); // error 3
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(15, 24),
                // (17,23): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (local, s) = (global, ""); // error 4
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "global").WithArguments("System.Span<int>").WithLocation(17, 23),
                // (18,23): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (local, s) = (global, null); // error 5
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "global").WithArguments("System.Span<int>").WithLocation(18, 23),
                // (20,19): error CS0306: The type 'Span<int>' may not be used as a type argument
                //         (c, s) = (local, ""); // error 6
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "local").WithArguments("System.Span<int>").WithLocation(20, 19)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void DeconstructionAssignmentToOuter(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M()
    {
        Span<int> outer = stackalloc int[10];

        {
            Span<int> local = stackalloc int[10]; // both stackallocs have the same escape scope
            (outer, outer) = outer;
            (outer, outer) = local;
            (outer, local) = local;
            (local, local) = local;
        }
    }
    public static void Main() => throw null;
}
public static class Extensions
{
    public static void Deconstruct(this Span<int> self, out Span<int> x, out Span<int> y) => throw null;
}
";

            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics();
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void DeconstructionDeclaration(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref Span<int> global)
    {
        Span<int> local = stackalloc int[10];
        var (local1, local2) = local;
        global = local1; // error 1
        global = local2; // error 2

        var (local3, local4) = global;
        global = local3;
        global = local4;
    }
    public static void Main() => throw null;
}
public static class Extensions
{
    public static void Deconstruct(this Span<int> self, out Span<int> x, out Span<int> y) => throw null;
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (10,18): error CS8352: Cannot use variable 'local1' in this context because it may expose referenced variables outside of their declaration scope
                //         global = local1; // error 1
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local1").WithArguments("local1").WithLocation(10, 18),
                // (11,18): error CS8352: Cannot use variable 'local2' in this context because it may expose referenced variables outside of their declaration scope
                //         global = local2; // error 2
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local2").WithArguments("local2").WithLocation(11, 18)
            );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeForeach(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref S global)
    {
        S localCollection = stackalloc int[10];
        foreach (var local in localCollection)
        {
            global = local; // error
        }
    }
    public static void Main() => throw null;
}

public ref struct S
{
    public S GetEnumerator() => throw null;
    public bool MoveNext() => throw null;
    public S Current => throw null;
    public static implicit operator S(Span<int> s) => throw null;
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (11,22): error CS8352: Cannot use variable 'local' in this context because it may expose referenced variables outside of their declaration scope
                //             global = local; // error
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local").WithArguments("local").WithLocation(11, 22)
                );
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeDeconstructionForeach(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref S global)
    {
        S localCollection = stackalloc int[10];
        foreach (var (local1, local2) in localCollection)
        {
            global = local1; // error 1
            global = local2; // error 2
        }
    }
    public static void Main() => throw null;
}

public ref struct S
{
    public S GetEnumerator() => throw null;
    public bool MoveNext() => throw null;
    public S Current => throw null;
    public static implicit operator S(Span<int> s) => throw null;
    public void Deconstruct(out S s1, out S s2) => throw null;
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (11,22): error CS8352: Cannot use variable 'local1' in this context because it may expose referenced variables outside of their declaration scope
                //             global = local1; // error 1
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local1").WithArguments("local1").WithLocation(11, 22),
                // (12,22): error CS8352: Cannot use variable 'local2' in this context because it may expose referenced variables outside of their declaration scope
                //             global = local2; // error 2
                Diagnostic(ErrorCode.ERR_EscapeVariable, "local2").WithArguments("local2").WithLocation(12, 22)
                );
        }

        [WorkItem(22361, "https://github.com/dotnet/roslyn/issues/22361")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeOutVarFromLocal(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref S global)
    {
        S local1 = stackalloc int[10];
        local1.M(out S local2); // we'd want this to succeed, but determine the safe-to-escape scope for local2 based on the invocation that declared it
        local1 = local2; // then this would be allowed
        global = local2; // and this would fail
    }
    public static void Main() => throw null;
}

public ref struct S
{
    public static implicit operator S(Span<int> s) => throw null;
    public void M(out S s) => throw null;
}
";
            // Tracking issue: https://github.com/dotnet/roslyn/issues/22361

            if (languageVersion == LanguageVersion.CSharp10)
            {
                CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                    // (9,9): error CS8352: Cannot use variable 'local1' in this context because it may expose referenced variables outside of their declaration scope
                    //         local1.M(out S local2);
                    Diagnostic(ErrorCode.ERR_EscapeVariable, "local1").WithArguments("local1").WithLocation(9, 9)
                    );
            }
            else
            {
                CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                    // (9,9): error CS8352: Cannot use variable 'local1' in this context because it may expose referenced variables outside of their declaration scope
                    //         local1.M(out S local2); // we'd want this to succeed, but determine the safe-to-escape scope for local2 based on the invocation that declared it
                    Diagnostic(ErrorCode.ERR_EscapeVariable, "local1").WithArguments("local1").WithLocation(9, 9),
                    // (9,9): error CS8350: This combination of arguments to 'S.M(out S)' is disallowed because it may expose variables referenced by parameter 'this' outside of their declaration scope
                    //         local1.M(out S local2); // we'd want this to succeed, but determine the safe-to-escape scope for local2 based on the invocation that declared it
                    Diagnostic(ErrorCode.ERR_CallArgMixing, "local1.M(out S local2)").WithArguments("S.M(out S)", "this").WithLocation(9, 9)
                    );
            }
        }

        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefLikeOutVarFromGlobal(LanguageVersion languageVersion)
        {
            var text = @"
using System;

public class C
{
    public void M(ref S global)
    {
        global.M(out S local2);
        global = local2;
    }
    public static void Main() => throw null;
}

public ref struct S
{
    public static implicit operator S(Span<int> s) => throw null;
    public void M(out S s) => throw null;
}
";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics();
        }

        [WorkItem(22456, "https://github.com/dotnet/roslyn/issues/22456")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void InMatchesIn(LanguageVersion languageVersion)
        {
            var text = @"
public class C
{
    public static void Main() => throw null;

    static ref readonly int F1(in int x)
    {
        return ref x; 
    }

    static ref readonly int Test1(in int x)
    {
        return ref F1(in x);
    }

    static ref readonly int Test2(in int x)
    {
        ref readonly var t = ref F1(in x);
        return ref t;
    }

    static ref readonly int Test3()
    {
        return ref F1(in (new int[1])[0]);
    }

    static ref readonly int Test4()
    {
        ref readonly var t = ref F1(in (new int[1])[0]);
        return ref t;
    }
}

";
            CreateCompilationWithMscorlibAndSpan(text, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics();
        }

        [WorkItem(24776, "https://github.com/dotnet/roslyn/issues/24776")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void PointerElementAccess_RefStructPointer(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
public ref struct TestStruct
{
    public void M() { }
}
public class C
{
    public static unsafe void Test(TestStruct[] ar)
    {
        fixed (TestStruct* p = ar)
        {
            for (int i = 0; i < ar.Length; i++)
            {
                p[i].M();
            }
        }
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeReleaseDll).VerifyDiagnostics(
                // (8,36): error CS0611: Array elements cannot be of type 'TestStruct'
                //     public static unsafe void Test(TestStruct[] ar)
                Diagnostic(ErrorCode.ERR_ArrayElementCantBeRefAny, "TestStruct").WithArguments("TestStruct").WithLocation(8, 36));
        }

        [WorkItem(24776, "https://github.com/dotnet/roslyn/issues/24776")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void PointerIndirectionOperator_RefStructPointer(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
public ref struct TestStruct
{
    public void M() { }
}
public class C
{
    public static unsafe void Test(TestStruct[] ar)
    {
        fixed (TestStruct* p = ar)
        {
            var x = *p;
        }
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.UnsafeReleaseDll).VerifyDiagnostics(
                // (8,36): error CS0611: Array elements cannot be of type 'TestStruct'
                //     public static unsafe void Test(TestStruct[] ar)
                Diagnostic(ErrorCode.ERR_ArrayElementCantBeRefAny, "TestStruct").WithArguments("TestStruct").WithLocation(8, 36));
        }

        [WorkItem(25398, "https://github.com/dotnet/roslyn/issues/25398")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void AwaitRefStruct(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
using System.Threading.Tasks;

ref struct S { }

class C
{
    async Task M(Task<S> t)
    {
        _ = await t;

        var a = await t;

        var r = t.Result;
        M(await t, ref r);
    }

    void M(S t, ref S t1)
    {
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics(
                // (8,26): error CS0306: The type 'S' may not be used as a type argument
                //     async Task M(Task<S> t)
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "t").WithArguments("S").WithLocation(8, 26),
                // (12,9): error CS4012: Parameters or locals of type 'S' cannot be declared in async methods or async lambda expressions.
                //         var a = await t;
                Diagnostic(ErrorCode.ERR_BadSpecialByRefLocal, "var").WithArguments("S").WithLocation(12, 9),
                // (14,9): error CS4012: Parameters or locals of type 'S' cannot be declared in async methods or async lambda expressions.
                //         var r = t.Result;
                Diagnostic(ErrorCode.ERR_BadSpecialByRefLocal, "var").WithArguments("S").WithLocation(14, 9),
                // (15,9): error CS8350: This combination of arguments to 'C.M(S, ref S)' is disallowed because it may expose variables referenced by parameter 't' outside of their declaration scope
                //         M(await t, ref r);
                Diagnostic(ErrorCode.ERR_CallArgMixing, "M(await t, ref r)").WithArguments("C.M(S, ref S)", "t").WithLocation(15, 9)
                );
        }

        [WorkItem(25398, "https://github.com/dotnet/roslyn/issues/25398")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void CoalesceRefStruct(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
ref struct S { }

class C
{
    void M()
    {       
        _ = (S?)null ?? default;

        var a = (S?)null ?? default;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics(
                // (8,14): error CS0306: The type 'S' may not be used as a type argument
                //         _ = (S?)null ?? default;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "S?").WithArguments("S").WithLocation(8, 14),
                // (10,18): error CS0306: The type 'S' may not be used as a type argument
                //         var a = (S?)null ?? default;
                Diagnostic(ErrorCode.ERR_BadTypeArgument, "S?").WithArguments("S").WithLocation(10, 18)
                );
        }

        [WorkItem(25398, "https://github.com/dotnet/roslyn/issues/25398")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ArrayAccessRefStruct(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
ref struct S { }

class C
{
    void M()
    {       
        _ = ((S[])null)[0];

        var a = ((S[])null)[0];
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics(
                // (8,15): error CS0611: Array elements cannot be of type 'S'
                //         _ = ((S[])null)[0];
                Diagnostic(ErrorCode.ERR_ArrayElementCantBeRefAny, "S").WithArguments("S").WithLocation(8, 15),
                // (10,19): error CS0611: Array elements cannot be of type 'S'
                //         var a = ((S[])null)[0];
                Diagnostic(ErrorCode.ERR_ArrayElementCantBeRefAny, "S").WithArguments("S").WithLocation(10, 19)
                );
        }

        [WorkItem(25398, "https://github.com/dotnet/roslyn/issues/25398")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ConditionalRefStruct(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
ref struct S { }

class C
{
    void M()
    {       
        _ = ((C)null)?.Test();

        var a = ((C)null)?.Test();
    }
    
    S Test() => default;        
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics(
                // (8,23): error CS8977: 'S' cannot be made nullable.
                //         _ = ((C)null)?.Test();
                Diagnostic(ErrorCode.ERR_CannotBeMadeNullable, ".Test()").WithArguments("S").WithLocation(8, 23),
                // (10,27): error CS8977: 'S' cannot be made nullable.
                //         var a = ((C)null)?.Test();
                Diagnostic(ErrorCode.ERR_CannotBeMadeNullable, ".Test()").WithArguments("S").WithLocation(10, 27)
                );
        }

        [WorkItem(25485, "https://github.com/dotnet/roslyn/issues/25485")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ArrayAccess_CrashesEscapeRules(LanguageVersion languageVersion)
        {
            CreateCompilationWithMscorlibAndSpan(@"
using System;
public class Class1
{
    public void Foo(Span<Thing>[] first, Thing[] second)
    {
        var x = first[0];
    }
}
public struct Thing
{
}
", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (5,21): error CS0611: Array elements cannot be of type 'Span<Thing>'
                //     public void Foo(Span<Thing>[] first, Thing[] second)
                Diagnostic(ErrorCode.ERR_ArrayElementCantBeRefAny, "Span<Thing>").WithArguments("System.Span<Thing>").WithLocation(5, 21));
        }

        [WorkItem(26457, "https://github.com/dotnet/roslyn/issues/26457")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefThisAssignment_Class(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
class Test
{
    public void M(ref Test obj)
    {
        this = ref this;
        obj = ref this;
        this = ref obj;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (6,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref this;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(6, 9),
                // (6,20): error CS1510: A ref or out value must be an assignable variable
                //         this = ref this;
                Diagnostic(ErrorCode.ERR_RefLvalueExpected, "this").WithLocation(6, 20),
                // (7,19): error CS1510: A ref or out value must be an assignable variable
                //         obj = ref this;
                Diagnostic(ErrorCode.ERR_RefLvalueExpected, "this").WithLocation(7, 19),
                // (8,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref obj;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(8, 9));
        }

        [WorkItem(26457, "https://github.com/dotnet/roslyn/issues/26457")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefThisAssignment_Struct(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
struct Test
{
    public void M(ref Test obj)
    {
        this = ref this;
        obj = ref this;
        this = ref obj;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (6,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref this;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(6, 9),
                // (7,9): error CS8374: Cannot ref-assign 'this' to 'obj' because 'this' has a narrower escape scope than 'obj'.
                //         obj = ref this;
                Diagnostic(ErrorCode.ERR_RefAssignNarrower, "obj = ref this").WithArguments("obj", "this").WithLocation(7, 9),
                // (8,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref obj;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(8, 9));
        }

        [WorkItem(26457, "https://github.com/dotnet/roslyn/issues/26457")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefThisAssignment_ReadOnlyStruct(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
readonly struct Test
{
    public void M(ref Test obj)
    {
        this = ref this;
        obj = ref this;
        this = ref obj;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (6,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref this;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(6, 9),
                // (7,19): error CS1510: A ref or out value must be an assignable variable
                //         obj = ref this;
                Diagnostic(ErrorCode.ERR_RefLvalueExpected, "this").WithLocation(7, 19),
                // (8,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref obj;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(8, 9));
        }

        [WorkItem(26457, "https://github.com/dotnet/roslyn/issues/26457")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefThisAssignment_RefStruct(LanguageVersion languageVersion)
        {
            var source = @"
ref struct Test
{
    public void M(ref Test obj)
    {
        this = ref this;
        obj = ref this;
        this = ref obj;
    }
}";

            CreateCompilation(source, parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (6,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref this;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(6, 9),
                // (7,9): error CS8374: Cannot ref-assign 'this' to 'obj' because 'this' has a narrower escape scope than 'obj'.
                //         obj = ref this;
                Diagnostic(ErrorCode.ERR_RefAssignNarrower, "obj = ref this").WithArguments("obj", "this").WithLocation(7, 9),
                // (8,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref obj;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(8, 9));
        }

        [WorkItem(26457, "https://github.com/dotnet/roslyn/issues/26457")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void RefThisAssignment_ReadOnlyRefStruct(LanguageVersion languageVersion)
        {
            CreateCompilation(@"
readonly ref struct Test
{
    public void M(ref Test obj)
    {
        this = ref this;
        obj = ref this;
        this = ref obj;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion)).VerifyDiagnostics(
                // (6,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref this;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(6, 9),
                // (7,19): error CS1510: A ref or out value must be an assignable variable
                //         obj = ref this;
                Diagnostic(ErrorCode.ERR_RefLvalueExpected, "this").WithLocation(7, 19),
                // (8,9): error CS8373: The left-hand side of a ref assignment must be a ref variable.
                //         this = ref obj;
                Diagnostic(ErrorCode.ERR_RefLocalOrParamExpected, "this").WithLocation(8, 9));
        }

        [WorkItem(29927, "https://github.com/dotnet/roslyn/issues/29927")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void CoalesceSpanReturn(LanguageVersion languageVersion)
        {
            CreateCompilationWithMscorlibAndSpan(@"
using System;
class C
{
    Span<byte> M()
    {       
        return null ?? new Span<byte>();
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics();
        }

        [WorkItem(29927, "https://github.com/dotnet/roslyn/issues/29927")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void CoalesceAssignSpanReturn(LanguageVersion languageVersion)
        {
            CreateCompilationWithMscorlibAndSpan(@"
using System;
class C
{
    Span<byte> M()
    {       
        var x = null ?? new Span<byte>();
        return x;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics();
        }

        [WorkItem(29927, "https://github.com/dotnet/roslyn/issues/29927")]
        [Theory]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void CoalesceRefSpanReturn(LanguageVersion languageVersion)
        {
            CreateCompilationWithMscorlibAndSpan(@"
using System;
class C
{
    Span<byte> M()
    {       
        Span<byte> x = stackalloc byte[10];
        return null ?? x;
    }
}", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics(
                // (8,24): error CS8352: Cannot use variable 'x' in this context because it may expose referenced variables outside of their declaration scope
                //         return null ?? x;
                Diagnostic(ErrorCode.ERR_EscapeVariable, "x").WithArguments("x").WithLocation(8, 24)
                );
        }

        [WorkItem(62973, "https://github.com/dotnet/roslyn/issues/62973")]
        [Fact]
        public void RegressionTest62973()
        {
            var compilation = CreateCompilation(
"""
#nullable enable
using System.Collections.Generic;

System.Console.WriteLine("");

public class ArrayPool<T> { }
public readonly ref struct PooledArrayHandle<T>
{
    public void Dispose() { }
}

public static class Test
{
    public static PooledArrayHandle<T> RentArray<T>(this int length, out T[] array, ArrayPool<T>? pool = null) {
        throw null!;
    }

    public static IEnumerable<int> Iterator() {
        // Verify that the ref struct is usable
        using var handle = RentArray<int>(200, out var array);
  
        for (int i = 0; i < array.Length; i++) {
            yield return i;
        }
    }
}
""");
            compilation.VerifyEmitDiagnostics(
                // (20,19): error CS4013: Instance of type 'PooledArrayHandle<int>' cannot be used inside a nested function, query expression, iterator block or async method
                //         using var handle = RentArray<int>(200, out var array);
                Diagnostic(ErrorCode.ERR_SpecialByRefInLambda, "handle = RentArray<int>(200, out var array)").WithArguments("PooledArrayHandle<int>").WithLocation(20, 19));
        }

        [Theory(Skip = "https://github.com/dotnet/roslyn/issues/40583")]
        [InlineData(LanguageVersion.CSharp10)]
        [InlineData(LanguageVersion.CSharp11)]
        public void ConvertedSpanReturn(LanguageVersion languageVersion)
        {
            CreateCompilationWithMscorlibAndSpan(@"
using System;
class C
{
    D M1() => stackalloc byte[10];
    D M2() { return stackalloc byte[10]; }
}
class D
{
    public static implicit operator D(Span<byte> span) => new D();
}
", parseOptions: TestOptions.Regular.WithLanguageVersion(languageVersion), options: TestOptions.ReleaseDll).VerifyDiagnostics();
        }

        [WorkItem(63384, "https://github.com/dotnet/roslyn/issues/63384")]
        [Theory]
        [InlineData("nuint")]
        [InlineData("nint")]
        public void NativeIntegerThis(string type)
        {
            var compilation = CreateCompilation(
    $$"""
        ref struct S
        {
            static int M({{type}} ptr) => ptr.GetHashCode();
        }
    """);

            compilation.VerifyDiagnostics();
        }

        [Fact]
        [WorkItem(63446, "https://github.com/dotnet/roslyn/issues/63446")]
        public void RefDiscardAssignment()
        {
            var source = @"
class Program
{
    static int dummy;

    static ref int F()
    {
        return ref dummy;
    }

    static void Main()
    {
        Test();
        System.Console.WriteLine(""Done"");
    }

    static void Test()
    {
        _ = ref F();
    }
}
";

            CompileAndVerify(source, expectedOutput: "Done").VerifyDiagnostics().
                VerifyIL("Program.Test",
@"
{
  // Code size        7 (0x7)
  .maxstack  1
  IL_0000:  call       ""ref int Program.F()""
  IL_0005:  pop
  IL_0006:  ret
}
");
        }
    }
}
