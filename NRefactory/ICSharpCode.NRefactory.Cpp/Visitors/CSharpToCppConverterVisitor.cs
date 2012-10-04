﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.Cpp.Ast;
using System.Reflection;
using Mono.Cecil;

namespace ICSharpCode.NRefactory.Cpp.Visitors
{
    public interface IEnvironmentProvider
    {
        string RootNamespace { get; }
        string GetTypeNameForAttribute(CSharp.Attribute attribute);
        TypeKind GetTypeKindForAstType(CSharp.AstType type);
        TypeCode ResolveExpression(CSharp.Expression expression);
        bool? IsReferenceType(CSharp.Expression expression);

        IType ResolveType(AstType type, TypeDeclaration entity = null);
    }

    public class CSharpToCppConverterVisitor : CSharp.IAstVisitor<object, Cpp.AstNode>
    {
        //Auxiliar list to change the array specifiers from one branch to another        
        private CSharp.TypeDeclaration currentType;
        private string currentMethod;
        private bool isInterface;

        IEnvironmentProvider provider;
        Stack<BlockStatement> blocks;
        Stack<TypeDeclaration> types;
        Stack<MemberInfo> members;


        public CSharpToCppConverterVisitor(IEnvironmentProvider provider)
        {
            this.provider = provider;
            this.blocks = new Stack<BlockStatement>();
            this.types = new Stack<TypeDeclaration>();
            this.members = new Stack<MemberInfo>();

            isInterface = false;
            Resolver.Restart();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitAnonymousMethodExpression(CSharp.AnonymousMethodExpression anonymousMethodExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUndocumentedExpression(CSharp.UndocumentedExpression undocumentedExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitArrayCreateExpression(CSharp.ArrayCreateExpression arrayCreateExpression, object data)
        {
            var expr = new ArrayCreateExpression()
            {
                Type = (AstType)arrayCreateExpression.Type.AcceptVisitor(this, data),
                Initializer = (ArrayInitializerExpression)arrayCreateExpression.Initializer.AcceptVisitor(this, data)
            };
            ConvertNodes(arrayCreateExpression.Arguments, expr.Arguments);
            ConvertNodes(arrayCreateExpression.AdditionalArraySpecifiers, expr.AdditionalArraySpecifiers);

            return EndNode(arrayCreateExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitArrayInitializerExpression(CSharp.ArrayInitializerExpression arrayInitializerExpression, object data)
        {
            ArrayInitializerExpression aiexp = new ArrayInitializerExpression();
            ConvertNodes(arrayInitializerExpression.Elements, aiexp.Elements);
            return aiexp;
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitAsExpression(CSharp.AsExpression asExpression, object data)
        {
            InvocationExpression invExpr = new InvocationExpression();
            IdentifierExpression mref = new IdentifierExpression();
            mref.TypeArguments.Add((AstType)asExpression.Type.AcceptVisitor(this, data));
            mref.Identifier = "as_cast";
            invExpr.Arguments.Add((Expression)asExpression.Expression.AcceptVisitor(this, data));
            invExpr.Target = mref;

            return EndNode(asExpression, invExpr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitAssignmentExpression(CSharp.AssignmentExpression assignmentExpression, object data)
        {
            var left = (Expression)assignmentExpression.Left.AcceptVisitor(this, data);
            var op = AssignmentOperatorType.Any;
            var right = (Expression)assignmentExpression.Right.AcceptVisitor(this, data);

            if (left is MemberReferenceExpression)
            {
                MemberReferenceExpression l = left as MemberReferenceExpression;
                if (Resolver.IsPropertyCall(l, currentType.Name))
                {
                    //SET
                    InvocationExpression m = new InvocationExpression(
                        new MemberReferenceExpression(l.Target.Clone(), "set" + l.MemberName), new Expression[1] { right.Clone() });
                    left = m;

                    return EndNode(assignmentExpression, m);
                }
            }

            if (right is MemberReferenceExpression)
            {
                MemberReferenceExpression r = right as MemberReferenceExpression;
                if (Resolver.IsPropertyCall(r, currentType.Name))
                {
                    //GET
                    InvocationExpression m = new InvocationExpression(
                        new MemberReferenceExpression(r.Target.Clone(), "get" + r.MemberName), new Expression[1] { new EmptyExpression() });
                    right = m;

                    return EndNode(assignmentExpression, m);
                }
            }

            switch (assignmentExpression.Operator)
            {
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.Assign:
                    op = AssignmentOperatorType.Assign;
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.Add:
                    op = AssignmentOperatorType.Add;
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.Subtract:
                    op = AssignmentOperatorType.Subtract;
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.Multiply:
                    op = AssignmentOperatorType.Multiply;
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.Divide:
                    op = AssignmentOperatorType.Divide;
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.Modulus:
                    op = AssignmentOperatorType.Assign;
                    right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.Modulus, right);
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.ShiftLeft:
                    op = AssignmentOperatorType.ShiftLeft;
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.ShiftRight:
                    op = AssignmentOperatorType.ShiftRight;
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.BitwiseAnd:
                    op = AssignmentOperatorType.Assign;
                    right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.BitwiseAnd, right);
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.BitwiseOr:
                    op = AssignmentOperatorType.Assign;
                    right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.BitwiseOr, right);
                    break;
                case ICSharpCode.NRefactory.CSharp.AssignmentOperatorType.ExclusiveOr:
                    op = AssignmentOperatorType.Assign;
                    right = new BinaryOperatorExpression((Expression)left.Clone(), BinaryOperatorType.ExclusiveOr, right);
                    break;
                default:
                    throw new Exception("Invalid value for AssignmentOperatorType: " + assignmentExpression.Operator);
            }

            var expr = new AssignmentExpression(left, op, right);
            return EndNode(assignmentExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitBaseReferenceExpression(CSharp.BaseReferenceExpression baseReferenceExpression, object data)
        {
            return EndNode(baseReferenceExpression, new BaseReferenceExpression());
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitBinaryOperatorExpression(CSharp.BinaryOperatorExpression binaryOperatorExpression, object data)
        {
            var left = (Expression)binaryOperatorExpression.Left.AcceptVisitor(this, data);
            var op = BinaryOperatorType.Any;
            var right = (Expression)binaryOperatorExpression.Right.AcceptVisitor(this, data);

            switch (binaryOperatorExpression.Operator)
            {
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.BitwiseAnd:
                    op = BinaryOperatorType.BitwiseAnd;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.BitwiseOr:
                    op = BinaryOperatorType.BitwiseOr;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.ConditionalAnd:
                    op = BinaryOperatorType.ConditionalAnd;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.ConditionalOr:
                    op = BinaryOperatorType.ConditionalOr;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.ExclusiveOr:
                    op = BinaryOperatorType.ExclusiveOr;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.GreaterThan:
                    op = BinaryOperatorType.GreaterThan;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.GreaterThanOrEqual:
                    op = BinaryOperatorType.GreaterThanOrEqual;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.Equality:
                    op = BinaryOperatorType.Equality;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.InEquality:
                    op = BinaryOperatorType.InEquality;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.LessThan:
                    op = BinaryOperatorType.LessThan;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.LessThanOrEqual:
                    op = BinaryOperatorType.LessThanOrEqual;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.Add:
                    // TODO might be string concatenation
                    op = BinaryOperatorType.Add;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.Subtract:
                    op = BinaryOperatorType.Subtract;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.Multiply:
                    op = BinaryOperatorType.Multiply;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.Divide:
                    op = BinaryOperatorType.Divide;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.Modulus:
                    op = BinaryOperatorType.Modulus;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.ShiftLeft:
                    op = BinaryOperatorType.ShiftLeft;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.ShiftRight:
                    op = BinaryOperatorType.ShiftRight;
                    break;
                case ICSharpCode.NRefactory.CSharp.BinaryOperatorType.NullCoalescing:
                    var nullCoalescing = new ConditionalExpression
                    {
                        TrueExpression = left,
                        FalseExpression = right
                    };
                    return EndNode(binaryOperatorExpression, nullCoalescing);
                default:
                    throw new Exception("Invalid value for BinaryOperatorType: " + binaryOperatorExpression.Operator);
            }

            return EndNode(binaryOperatorExpression, new BinaryOperatorExpression(left, op, right));
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCastExpression(CSharp.CastExpression castExpression, object data)
        {
            CastExpression cexp = new CastExpression((AstType)castExpression.Type.AcceptVisitor(this, data), (Expression)castExpression.Expression.AcceptVisitor(this, data));
            return EndNode(castExpression, cexp);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCheckedExpression(CSharp.CheckedExpression checkedExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitConditionalExpression(CSharp.ConditionalExpression conditionalExpression, object data)
        {
            var cExpr = new ConditionalExpression(
                (Expression)conditionalExpression.Condition.AcceptVisitor(this, data),
                (Expression)conditionalExpression.TrueExpression.AcceptVisitor(this, data),
                (Expression)conditionalExpression.FalseExpression.AcceptVisitor(this, data)
                );
            return EndNode(conditionalExpression, cExpr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitDefaultValueExpression(CSharp.DefaultValueExpression defaultValueExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitDirectionExpression(CSharp.DirectionExpression directionExpression, object data)
        {
            return EndNode(directionExpression, (Expression)directionExpression.Expression.AcceptVisitor(this, data));
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitIdentifierExpression(CSharp.IdentifierExpression identifierExpression, object data)
        {
            bool needsPointer = Resolver.NeedsDereference(identifierExpression, currentType == null ? String.Empty : currentType.Name, currentMethod);

            var expr = new IdentifierExpression();
            expr.Identifier = identifierExpression.Identifier;
            ConvertNodes(identifierExpression.TypeArguments, expr.TypeArguments);

            if (needsPointer)
                return EndNode(identifierExpression, new PointerExpression() { Target = expr });
            return EndNode(identifierExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitIndexerExpression(CSharp.IndexerExpression indexerExpression, object data)
        {
            //Check if the identifier is a pointer type: if it is a pointer, we have to de-reference it to apply an indexer operator: data[i]; NO!!! ------ (*data)[i]; YES !!
            bool needsDeref = Resolver.NeedsDereference(indexerExpression, currentType.Name, currentMethod);

            var expr = new IndexerExpression((Expression)indexerExpression.Target.AcceptVisitor(this, data));
            ConvertNodes(indexerExpression.Arguments, expr.Arguments);
            if (needsDeref)
                expr.Target = new PointerExpression((Expression)expr.Target.Clone());

            return EndNode(indexerExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitInvocationExpression(CSharp.InvocationExpression invocationExpression, object data)
        {
            if (invocationExpression.Target is CSharp.MemberReferenceExpression)
            {
                var mref = invocationExpression.Target as CSharp.MemberReferenceExpression;
                if (mref.MemberName == "ToString")
                {
                    //TODO: Only if the invocationExpression.Target returns a basic type !!!
                    //TODO: The type is extracted from the annotations.
                    //We must be careful, we have supposed the annotations always contain that information, and may be not !!
                    if (mref.Target.Annotations.Any())
                    {
                        for (int i = 0; i < mref.Target.Annotations.Count(); i++)
                        {
                            if (mref.Target.Annotations.ElementAt(i) is Decompiler.Ast.TypeInformation)
                            {
                                Decompiler.Ast.TypeInformation t = mref.Target.Annotations.ElementAt(i) as Decompiler.Ast.TypeInformation;
                                if (t.InferredType.IsPrimitive)
                                {
                                    var _expr = new ObjectCreateExpression(new SimpleType("String"), (Expression)mref.Target.AcceptVisitor(this, data));
                                    return EndNode(invocationExpression, _expr);
                                }
                            }
                        }
                    }
                }
            }

            var expr = new InvocationExpression(
                (Expression)invocationExpression.Target.AcceptVisitor(this, data));

            ConvertNodes(invocationExpression.Arguments, expr.Arguments);
            for (int i = 0; i < expr.Arguments.Count; i++)
            {
                Expression ex = expr.Arguments.ElementAt(i);
                if (ex is MemberReferenceExpression)
                {
                    MemberReferenceExpression mre = ex as MemberReferenceExpression;

                    if (Resolver.IsPropertyCall(mre, currentType.Name))
                    {
                        //GET
                        InvocationExpression m = new InvocationExpression(
                            new MemberReferenceExpression(mre.Target.Clone(), "get" + mre.MemberName), new Expression[1] { new EmptyExpression() });

                        expr.Arguments.InsertAfter(expr.Arguments.ElementAt(i), m);
                        expr.Arguments.Remove(expr.Arguments.ElementAt(i));
                    }
                }
            }

            return EndNode(invocationExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitIsExpression(CSharp.IsExpression isExpression, object data)
        {
            InvocationExpression invExpr = new InvocationExpression();
            IdentifierExpression mref = new IdentifierExpression();
            mref.TypeArguments.Add((AstType)isExpression.Type.AcceptVisitor(this, data));
            mref.Identifier = "is_inst_of";
            invExpr.Arguments.Add((Expression)isExpression.Expression.AcceptVisitor(this, data));
            invExpr.Target = mref;

            return EndNode(isExpression, invExpr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitLambdaExpression(CSharp.LambdaExpression lambdaExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitMemberReferenceExpression(CSharp.MemberReferenceExpression memberReferenceExpression, object data)
        {
            var mref = new MemberReferenceExpression();
            mref.Target = (Expression)memberReferenceExpression.Target.AcceptVisitor(this, data);
            mref.MemberName = memberReferenceExpression.MemberName;
            ConvertNodes(memberReferenceExpression.TypeArguments, mref.TypeArguments);

            return EndNode(memberReferenceExpression, mref);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitNamedArgumentExpression(CSharp.NamedArgumentExpression namedArgumentExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitNamedExpression(CSharp.NamedExpression namedExpression, object data)
        {
            NamedExpression nexpr = new NamedExpression(namedExpression.Identifier, (Expression)namedExpression.Expression.AcceptVisitor(this, data));
            return EndNode(namedExpression, nexpr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitNullReferenceExpression(CSharp.NullReferenceExpression nullReferenceExpression, object data)
        {
            return EndNode(nullReferenceExpression, new NullReferenceExpression());
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitObjectCreateExpression(CSharp.ObjectCreateExpression objectCreateExpression, object data)
        {
            //When a variable is initialized List* list = new List(); first List is pointer type, second List is SimpleType
            bool isGcPtr = false;
            var type = (AstType)objectCreateExpression.Type.AcceptVisitor(this, data);
            if (type is PtrType)//Here we make the change
            {
                PtrType ptr = type as PtrType;
                type = (AstType)ptr.Target.Clone();
                isGcPtr = true;
            }

            var expr = new ObjectCreateExpression(type);
            ConvertNodes(objectCreateExpression.Arguments, expr.Arguments);
            expr.isGCPtr = isGcPtr;
            if (!objectCreateExpression.Initializer.IsNull)
                expr.Initializer = (ArrayInitializerExpression)objectCreateExpression.Initializer.AcceptVisitor(this, data);

            return EndNode(objectCreateExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitAnonymousTypeCreateExpression(CSharp.AnonymousTypeCreateExpression anonymousTypeCreateExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitParenthesizedExpression(CSharp.ParenthesizedExpression parenthesizedExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitPointerReferenceExpression(CSharp.PointerReferenceExpression pointerReferenceExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitPrimitiveExpression(CSharp.PrimitiveExpression primitiveExpression, object data)
        {
            Expression expr;

            //Testing...
            if (primitiveExpression.Value is string)
            {
                return EndNode(primitiveExpression, new ObjectCreateExpression(
                    new SimpleType("String"),
                    new PrimitiveExpression(primitiveExpression.Value as string)));
            }

            //if (!string.IsNullOrEmpty(primitiveExpression.Value as string) || primitiveExpression.Value is char)
            //    expr = new PrimitiveExpression(primitiveExpression.Value);//TODO
            ////expr = ConvertToConcat(primitiveExpression.Value.ToString());
            //else
            //    expr = new PrimitiveExpression(primitiveExpression.Value);

            expr = new PrimitiveExpression(primitiveExpression.Value);
            return EndNode(primitiveExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitSizeOfExpression(CSharp.SizeOfExpression sizeOfExpression, object data)
        {
            var sizeofExpr = new SizeOfExpression();
            sizeofExpr.Type = (AstType)sizeOfExpression.Type.AcceptVisitor(this, data);
            return EndNode(sizeOfExpression, sizeofExpr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitStackAllocExpression(CSharp.StackAllocExpression stackAllocExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitThisReferenceExpression(CSharp.ThisReferenceExpression thisReferenceExpression, object data)
        {
            return EndNode(thisReferenceExpression, new ThisReferenceExpression());
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitTypeOfExpression(CSharp.TypeOfExpression typeOfExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitTypeReferenceExpression(CSharp.TypeReferenceExpression typeReferenceExpression, object data)
        {
            var expr = new TypeReferenceExpression((AstType)typeReferenceExpression.Type.AcceptVisitor(this, data));
            return EndNode(typeReferenceExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUnaryOperatorExpression(CSharp.UnaryOperatorExpression unaryOperatorExpression, object data)
        {
            Expression expr;

            switch (unaryOperatorExpression.Operator)
            {
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.Not:
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.BitNot:
                    expr = new UnaryOperatorExpression()
                    {
                        Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                        Operator = UnaryOperatorType.Not
                    };
                    break;
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.Minus:
                    expr = new UnaryOperatorExpression()
                    {
                        Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                        Operator = UnaryOperatorType.Minus
                    };
                    break;
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.Plus:
                    expr = new UnaryOperatorExpression()
                    {
                        Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                        Operator = UnaryOperatorType.Plus
                    };
                    break;
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.Increment:
                    expr = new UnaryOperatorExpression()
                   {
                       Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                       Operator = UnaryOperatorType.Increment
                   };
                    break;
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.PostIncrement:
                    expr = new UnaryOperatorExpression()
                    {
                        Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                        Operator = UnaryOperatorType.PostIncrement
                    };
                    break;
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.Decrement:
                    expr = new UnaryOperatorExpression()
                    {
                        Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                        Operator = UnaryOperatorType.Decrement
                    };
                    break;
                case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.PostDecrement:
                    expr = new UnaryOperatorExpression()
                    {
                        Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                        Operator = UnaryOperatorType.PostDecrement
                    };
                    break;
                //case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.AddressOf:
                //    expr = new UnaryOperatorExpression()
                //    {
                //        Expression = (Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data),
                //        Operator = UnaryOperatorType.AddressOf
                //    };
                //    break;
                //case ICSharpCode.NRefactory.CSharp.UnaryOperatorType.Dereference:
                //    expr = new InvocationExpression();
                //    ((InvocationExpression)expr).Target = new IdentifierExpression() { Identifier = "__Dereference" };
                //    ((InvocationExpression)expr).Arguments.Add((Expression)unaryOperatorExpression.Expression.AcceptVisitor(this, data));
                //    break;
                default:
                    throw new Exception("Invalid value for UnaryOperatorType");
            }

            return EndNode(unaryOperatorExpression, expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUncheckedExpression(CSharp.UncheckedExpression uncheckedExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitEmptyExpression(CSharp.EmptyExpression emptyExpression, object data)
        {
            var eexpr = new EmptyExpression();
            CopyAnnotations(emptyExpression, eexpr);
            return EndNode(emptyExpression, eexpr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryExpression(CSharp.QueryExpression queryExpression, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryContinuationClause(CSharp.QueryContinuationClause queryContinuationClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryFromClause(CSharp.QueryFromClause queryFromClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryLetClause(CSharp.QueryLetClause queryLetClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryWhereClause(CSharp.QueryWhereClause queryWhereClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryJoinClause(CSharp.QueryJoinClause queryJoinClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryOrderClause(CSharp.QueryOrderClause queryOrderClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryOrdering(CSharp.QueryOrdering queryOrdering, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQuerySelectClause(CSharp.QuerySelectClause querySelectClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitQueryGroupClause(CSharp.QueryGroupClause queryGroupClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitAttribute(CSharp.Attribute attribute, object data)
        {
            var attr = new Cpp.Ast.Attribute();
            //AttributeTarget target;
            //attr.A = target;
            attr.Type = (AstType)attribute.Type.AcceptVisitor(this, data);
            ConvertNodes(attribute.Arguments, attr.Arguments);

            return EndNode(attribute, attr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitAttributeSection(CSharp.AttributeSection attributeSection, object data)
        {
            AttributeSection attrSection = new AttributeSection();
            ConvertNodes(attributeSection.Attributes, attrSection.Attributes);
            attrSection.AttributeTargetToken = (Identifier)attributeSection.AttributeTargetToken.AcceptVisitor(this, data);
            return EndNode(attributeSection, attrSection);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitDelegateDeclaration(CSharp.DelegateDeclaration delegateDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitNamespaceDeclaration(CSharp.NamespaceDeclaration namespaceDeclaration, object data)
        {
            var newNamespace = new NamespaceDeclaration();

            ConvertNodes(namespaceDeclaration.Identifiers, newNamespace.Identifiers);
            ConvertNodes(namespaceDeclaration.Members, newNamespace.Members);

            return EndNode(namespaceDeclaration, newNamespace);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitTypeDeclaration(CSharp.TypeDeclaration typeDeclaration, object data)
        {
            currentType = typeDeclaration;
            var type = new TypeDeclaration();
            CopyAnnotations(typeDeclaration, type);

            CSharp.Attribute stdModAttr;

            if (typeDeclaration.ClassType == CSharp.ClassType.Class && HasAttribute(typeDeclaration.Attributes, "Microsoft.VisualBasic.CompilerServices.StandardModuleAttribute", out stdModAttr))
            {
                type.ClassType = ClassType.Class;
                // remove AttributeSection if only one attribute is present
                var attrSec = (CSharp.AttributeSection)stdModAttr.Parent;
                if (attrSec.Attributes.Count == 1)
                    attrSec.Remove();
                else
                    stdModAttr.Remove();
            }
            else
            {
                switch (typeDeclaration.ClassType)
                {
                    case CSharp.ClassType.Class:
                        isInterface = false;
                        type.ClassType = ClassType.Class;
                        break;
                    case CSharp.ClassType.Struct:
                        isInterface = false;
                        type.ClassType = ClassType.Struct;
                        break;
                    case CSharp.ClassType.Interface:
                        isInterface = true;
                        type.ClassType = ClassType.Interface;
                        break;
                    case CSharp.ClassType.Enum:
                        isInterface = false;
                        type.ClassType = ClassType.Enum;
                        break;
                    default:
                        throw new InvalidOperationException("Invalid value for ClassType");
                }
            }

            if ((typeDeclaration.Modifiers & CSharp.Modifiers.Static) == CSharp.Modifiers.Static)
            {
                type.ClassType = ClassType.Class;
                typeDeclaration.Modifiers &= ~CSharp.Modifiers.Static;
            }

            ConvertNodes(typeDeclaration.Attributes, type.Attributes);
            ConvertNodes(typeDeclaration.ModifierTokens, type.ModifierTokens);

            if (typeDeclaration.BaseTypes.Any())
                ConvertNodes(typeDeclaration.BaseTypes, type.BaseTypes);

            type.Name = typeDeclaration.Name;
            if (typeDeclaration.TypeParameters.Any())
                type.Name += "_T";

            types.Push(type);
            ConvertNodes(typeDeclaration.Members, type.Members);
            types.Pop();

            //Add auxiliar variables for emptyproperties
            foreach (KeyValuePair<string, AstType> kvp in Cache.GetAuxVariables())
            {
                FieldDeclaration fdecl = new FieldDeclaration();
                fdecl.ReturnType = (AstType)kvp.Value.Clone();
                fdecl.AddChild(new VariableInitializer(kvp.Key), FieldDeclaration.Roles.Variable);
                type.AddChild(fdecl, TypeDeclaration.MemberRole);
                Cache.AddConstructorStatement(new ExpressionStatement(
                    new AssignmentExpression(
                        new IdentifierExpression(kvp.Key), new PrimitiveExpression(0))));

                HeaderFieldDeclaration hf = new HeaderFieldDeclaration();
                Resolver.GetHeaderNode(fdecl, hf);
                Cache.AddHeaderNode(hf);
            }

            //Add constructor if there is no added yet and it is necessary to initialize some variables
            if (Cache.GetConstructorStatements().Any())
            {

                IEnumerable<ConstructorDeclaration> tmpList = type.Members.OfType<ConstructorDeclaration>();
                //Is there any constructor ?
                if (tmpList.Any())
                {
                    //Take the constructor and add the satements
                    foreach (Statement st in Cache.GetConstructorStatements())
                        tmpList.ElementAt(0).Body.Statements.InsertBefore(tmpList.ElementAt(0).Body.FirstOrDefault(), (Statement)st.Clone());
                }
                else //CONSTRUCTOR DOES NOT EXIST
                {
                    ConstructorDeclaration result = new ConstructorDeclaration();
                    result.ModifierTokens.Add(new CppModifierToken(TextLocation.Empty, Modifiers.Public));
                    BlockStatement blck = new BlockStatement();
                    foreach (Statement st in Cache.GetConstructorStatements())
                        blck.AddChild((Statement)st.Clone(), BlockStatement.StatementRole);
                    result.Body = blck;
                    Cache.ClearConstructorStatements();
                    result.Name = type.Name;
                    result.IdentifierToken = new Identifier(type.Name, TextLocation.Empty);

                    type.AddChild(result, TypeDeclaration.MemberRole);

                    HeaderConstructorDeclaration hc = new HeaderConstructorDeclaration();
                    Resolver.GetHeaderNode(result, hc);
                    Cache.AddHeaderNode(hc);
                }
            }

            Cache.ClearAuxVariables();

            if (typeDeclaration.TypeParameters.Any())
                ConvertNodes(typeDeclaration.TypeParameters, type.TypeParameters);

            Resolver.ProcessIncludes(type.Name);

            //HERE SHOULD BE BaseType or InheritedType or something similar
            AstType objectType = new SimpleType("Object");
            AstType gcType = new SimpleType("gc_cleanup");
            type.AddChild(objectType, TypeDeclaration.BaseTypeRole);
            type.AddChild(gcType, TypeDeclaration.BaseTypeRole);

            //Fill the nested types
            Resolver.GetNestedTypes(type);

            Cache.AddNamespace("System");

            //TODO: I'm not sure...
            if (isInterface)
                return EndNode(typeDeclaration, new InterfaceTypeDeclaration(type));

            if (type.TypeParameters.Any())
            {
                GenericTemplateTypeDeclaration gtempl = new GenericTemplateTypeDeclaration();
                BaseTemplateTypeDeclaration btempl = new BaseTemplateTypeDeclaration();
                TemplateTypeDeclaration ttempl = new TemplateTypeDeclaration();
                SpecializedBasicTemplateDeclaration spec = new SpecializedBasicTemplateDeclaration();
                SpecializedGenericTemplateDeclaration specGen = new SpecializedGenericTemplateDeclaration();
                GenericEntryPointDeclaration genEntry = new GenericEntryPointDeclaration();

                btempl.Name = ttempl.Name = spec.Name = specGen.Name = genEntry.Name = gtempl.Name = type.Name;

                foreach (var mod in type.ModifierTokens)
                {
                    btempl.ModifierTokens.Add((CppModifierToken)mod.Clone());
                    ttempl.ModifierTokens.Add((CppModifierToken)mod.Clone());
                    spec.ModifierTokens.Add((CppModifierToken)mod.Clone());
                    specGen.ModifierTokens.Add((CppModifierToken)mod.Clone());
                    genEntry.ModifierTokens.Add((CppModifierToken)mod.Clone());
                }

                /***************** TYPE PARAMETERS *****************/
                foreach (var typePar in type.TypeParameters)
                {
                    btempl.TypeParameters.Add((TypeParameterDeclaration)typePar.Clone());
                    spec.TypeParameters.Add((TypeParameterDeclaration)typePar.Clone());
                    specGen.TypeParameters.Add((TypeParameterDeclaration)typePar.Clone());
                    genEntry.TypeParameters.Add((TypeParameterDeclaration)typePar.Clone());
                }
                List<AstType> tmp = new List<AstType>() { new TypeNameType(new SimpleType("T")), new PrimitiveType("bool") };
                ttempl.TypeParameters.AddRange(tmp);


                /***************** BASE TYPES *****************/
                foreach (AstType baseType in type.BaseTypes)
                {
                    //If the base class is a template type, we have to dereference the type if it is a basic type
                    //The template DeRefType<typename T> provides that conversion
                    if (baseType is SimpleType)
                    {
                        for (int i = 0; i < (baseType as SimpleType).TypeArguments.Count; i++)
                        {
                            AstType arg = (AstType)(baseType as SimpleType).TypeArguments.ElementAt(i);
                            if (Resolver.IsTemplateType(arg))
                            {
                                InvocationExpression ic = new InvocationExpression(new IdentifierExpression("TypeArg"), new IdentifierExpression(Resolver.GetTypeName(arg)));
                                ExpressionType exprT = new ExpressionType(ic);
                                (baseType as SimpleType).TypeArguments.InsertAfter(arg, exprT);
                                (baseType as SimpleType).TypeArguments.Remove(arg);
                            }
                        }
                    }
                }

                foreach (var btype in type.BaseTypes)
                    btempl.BaseTypes.Add((AstType)btype.Clone());

                List<Expression> tmp_args = new List<Expression>();
                //WHAT TO DO IF THERE ARE MORE THAN ONE TEMPLATE TYPE ?
                //MAYBE FOR EACH TYPE: template< typename T1, bool isT1Basic, typename T2, bool isT2Basic ... ??
                if (genEntry.TypeParameters.Count > 1)
                    throw new NotImplementedException("Not supported yet");

                foreach (var typePar in genEntry.TypeParameters)
                {
                    Expression argument = new IdentifierExpression(typePar.Name);
                    tmp_args.Add(argument);
                }
                InvocationExpression _ic = new InvocationExpression(new IdentifierExpression("IsBasic"), tmp_args);
                ExpressionType _exprT = new ExpressionType(_ic);

                //ENTRY POINT
                SimpleType _base = new SimpleType(genEntry.Name);
                _base.TypeArguments.Add(new ExpressionType((Expression)tmp_args[0].Clone()));
                _base.TypeArguments.Add(_exprT);
                genEntry.BaseTypes.Add(new QualifiedType(new SimpleType("_Internal"), new Identifier(_base.ToString(), TextLocation.Empty)));

                //GENERIC SPECIALIZATION
                SimpleType spec_gen_super = new SimpleType(specGen.Name + "_Base");
                spec_gen_super.TypeArguments.Add(new PtrType(new SimpleType("Object")));
                specGen.BaseTypes.Add(spec_gen_super);

                //BASIC SPECIALIZATION
                SimpleType b = new SimpleType(spec.Name + "_Base");
                b.TypeArguments.Add(new SimpleType("T"));
                spec.BaseTypes.Add(b);

                bool hasDefaultConstructor = false;
                /***************** MEMBERS *****************/
                foreach (var member in type.Members)
                {
                    //Add the member to the base template before modify anything
                    btempl.Members.Add((AttributedNode)member.Clone());
                    if (member is ConstructorDeclaration)
                    {
                        String naturalName = genEntry.Name.TrimEnd("_Base".ToCharArray()).TrimEnd("_T".ToCharArray());
                        hasDefaultConstructor = true;
                        //********************ENTRY POINT
                        var constr = member as ConstructorDeclaration;
                        if(!constr.HasModifier(Modifiers.Public))
                            constr.ModifierTokens.Add(new CppModifierToken(TextLocation.Empty,Modifiers.Public));

                        constr.Body = new BlockStatement();
                        constr.Initializer = new ConstructorInitializer();
                        constr.Initializer.Base = (AstType)genEntry.BaseTypes.ElementAt(0).Clone();
                        constr.Name = naturalName;

                        foreach (var arg in constr.Parameters)
                        {
                            hasDefaultConstructor = false;
                            constr.Initializer.Arguments.Add(new IdentifierExpression(arg.Name));
                        }

                        genEntry.Members.Add((AttributedNode)constr.Clone());

                        //********************GENERIC SPECIALIZATION
                        var spec_constr = member as ConstructorDeclaration;

                        spec_constr.Body = new BlockStatement();
                        spec_constr.Initializer = new ConstructorInitializer();
                        spec_constr.Initializer.Base = (AstType)specGen.BaseTypes.ElementAt(0).Clone();
                        spec_constr.Name = naturalName;

                        foreach (var arg in constr.Parameters)
                        {
                            AstType _tmp;
                            Resolver.TryPatchTemplateToObjectType(arg.Type, out _tmp);
                            constr.Initializer.Arguments.Add(new CastExpression(_tmp, new IdentifierExpression(arg.Name)));
                        }

                        specGen.Members.Add((AttributedNode)spec_constr.Clone());

                        //********************BASIC SPECIALIZATION
                        var spec_B_constr = member as ConstructorDeclaration;

                        spec_B_constr.Body = new BlockStatement();
                        spec_B_constr.Initializer = new ConstructorInitializer();
                        spec_B_constr.Initializer.Base = (AstType)spec.BaseTypes.ElementAt(0).Clone();
                        spec_B_constr.Name = naturalName;

                        foreach (var arg in constr.Parameters)
                            constr.Initializer.Arguments.Add(new IdentifierExpression(arg.Name));

                        spec.Members.Add((AttributedNode)spec_B_constr.Clone());
                    }
                    else
                    {//ADD ALL MEMBERS BUT NOT THE CONSTRUCTORS
                        specGen.Members.Add((AttributedNode)member.Clone());
                    }                   
                    ttempl.Members.Add((AttributedNode)member.Clone());
                    
                }

                if (!hasDefaultConstructor)
                {
                    var def_const = new ConstructorDeclaration();
                    def_const.Name = btempl.Name.TrimEnd("_Base".ToArray()).TrimEnd("_T".ToCharArray());//REMEMBER IN THE MEMBERS IS BETTER TO STORE THE ORIGINAL TYPE NAME
                    def_const.ModifierTokens.Add(new CppModifierToken(TextLocation.Empty, Modifiers.Public));
                    def_const.Body = new BlockStatement();
                    btempl.Members.Add(def_const);
                }               

                /**************** FILL THE GENERIC TEMPLATE TYPE ***************/
                gtempl.TypeDefinition = genEntry;
                gtempl.Members.Add(btempl);
                gtempl.Members.Add(ttempl);
                gtempl.Members.Add(spec);
                gtempl.Members.Add(specGen);

                return EndNode(typeDeclaration, gtempl);
            }


            return EndNode(typeDeclaration, type);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUsingAliasDeclaration(CSharp.UsingAliasDeclaration usingAliasDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUsingDeclaration(CSharp.UsingDeclaration usingDeclaration, object data)
        {
            AstType import = (AstType)usingDeclaration.Import.AcceptVisitor(this, data);
            var include = new IncludeDeclaration(import);

            return EndNode(usingDeclaration, include);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitExternAliasDeclaration(CSharp.ExternAliasDeclaration externAliasDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitBlockStatement(CSharp.BlockStatement blockStatement, object data)
        {
            var block = new BlockStatement();
            blocks.Push(block);
            ConvertNodes(blockStatement, block.Statements);
            blocks.Pop();
            return EndNode(blockStatement, block);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitBreakStatement(CSharp.BreakStatement breakStatement, object data)
        {
            return EndNode(breakStatement, new BreakStatement());
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCheckedStatement(CSharp.CheckedStatement checkedStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitContinueStatement(CSharp.ContinueStatement continueStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitDoWhileStatement(CSharp.DoWhileStatement doWhileStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitEmptyStatement(CSharp.EmptyStatement emptyStatement, object data)
        {
            return EndNode(emptyStatement, new EmptyExpression());
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitExpressionStatement(CSharp.ExpressionStatement expressionStatement, object data)
        {
            var _expr = new ExpressionStatement((Expression)expressionStatement.Expression.AcceptVisitor(this, data));
            return EndNode(expressionStatement, _expr);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitFixedStatement(CSharp.FixedStatement fixedStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitForeachStatement(CSharp.ForeachStatement foreachStatement, object data)
        {
            ForeachStatement feach = new ForeachStatement();

            //Add variable declaration to the foreach body (in order to dereference from iterator to the variable)
            string tmpVar = "_" + foreachStatement.VariableName.ToUpper();
            VariableDeclarationStatement vds = new VariableDeclarationStatement(
                (AstType)foreachStatement.VariableType.AcceptVisitor(this, data),
                foreachStatement.VariableName,
                new PointerExpression(new IdentifierExpression(tmpVar)));

            BlockStatement blckstmt = new BlockStatement();
            blckstmt.AddChild(vds, BlockStatement.StatementRole);
            foreach (CSharp.Statement st in foreachStatement.EmbeddedStatement.GetChildrenByRole(CSharp.BlockStatement.StatementRole))
                blckstmt.AddChild((Statement)st.AcceptVisitor(this, data), BlockStatement.StatementRole);
            feach.ForEachStatement = blckstmt;

            feach.VariableIdentifier = new Identifier(tmpVar, TextLocation.Empty);
            feach.CollectionExpression = (Expression)foreachStatement.InExpression.AcceptVisitor(this, data);

            return EndNode(foreachStatement, feach);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitForStatement(CSharp.ForStatement forStatement, object data)
        {
            ForStatement for_stmt = new ForStatement();
            for_stmt.Condition = (Expression)forStatement.Condition.AcceptVisitor(this, data);
            for_stmt.EmbeddedStatement = (Statement)forStatement.EmbeddedStatement.AcceptVisitor(this, data);
            ConvertNodes(forStatement.Initializers, for_stmt.Initializers);
            ConvertNodes(forStatement.Iterators, for_stmt.Iterators);
            return EndNode(forStatement, for_stmt);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitGotoCaseStatement(CSharp.GotoCaseStatement gotoCaseStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitGotoDefaultStatement(CSharp.GotoDefaultStatement gotoDefaultStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitGotoStatement(CSharp.GotoStatement gotoStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitIfElseStatement(CSharp.IfElseStatement ifElseStatement, object data)
        {
            var stmt = new IfElseStatement();

            stmt.Condition = (Expression)ifElseStatement.Condition.AcceptVisitor(this, data);
            stmt.TrueStatement = (Statement)ifElseStatement.TrueStatement.AcceptVisitor(this, data);
            stmt.FalseStatement = (Statement)ifElseStatement.FalseStatement.AcceptVisitor(this, data);

            return EndNode(ifElseStatement, stmt);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitLabelStatement(CSharp.LabelStatement labelStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitLockStatement(CSharp.LockStatement lockStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitReturnStatement(CSharp.ReturnStatement returnStatement, object data)
        {
            var expr = (Expression)returnStatement.Expression.AcceptVisitor(this, data);


            if (expr is MemberReferenceExpression)
            {
                MemberReferenceExpression r = expr as MemberReferenceExpression;
                if (Resolver.IsPropertyCall(r, currentType.Name))
                {
                    //GET
                    expr = new InvocationExpression(
                        new MemberReferenceExpression(r.Target.Clone(), "get" + r.MemberName), new Expression[1] { new EmptyExpression() });
                }
            }

            var stmt = new ReturnStatement(expr);
            return EndNode(returnStatement, stmt);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitSwitchStatement(CSharp.SwitchStatement switchStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitSwitchSection(CSharp.SwitchSection switchSection, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCaseLabel(CSharp.CaseLabel caseLabel, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitThrowStatement(CSharp.ThrowStatement throwStatement, object data)
        {
            return EndNode(throwStatement, new ThrowStatement((Expression)throwStatement.Expression.AcceptVisitor(this, data)));
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitTryCatchStatement(CSharp.TryCatchStatement tryCatchStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCatchClause(CSharp.CatchClause catchClause, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUncheckedStatement(CSharp.UncheckedStatement uncheckedStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUnsafeStatement(CSharp.UnsafeStatement unsafeStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitUsingStatement(CSharp.UsingStatement usingStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitVariableDeclarationStatement(CSharp.VariableDeclarationStatement variableDeclarationStatement, object data)
        {
            bool objectCreation = false;
            bool arrayCreation = false;
            var vds = new VariableDeclarationStatement();

            if (variableDeclarationStatement.Type is CSharp.ComposedType)
            {
                if ((variableDeclarationStatement.Type as CSharp.ComposedType).ArraySpecifiers.Any())
                {
                    if (variableDeclarationStatement.Variables.Count == 1)
                    {
                        CSharp.VariableInitializer v = variableDeclarationStatement.Variables.ElementAt(0);
                        //We must check the array for any of the expression that can return values (objet creations, array creations, invocations)
                        if (v.Initializer is CSharp.ObjectCreateExpression)
                        {
                            objectCreation = true;
                        }
                        else if (v.Initializer is CSharp.ArrayCreateExpression || v.Initializer is CSharp.InvocationExpression)
                        {
                            arrayCreation = true;
                        }

                    }
                }
            }
            //else if(variableDeclarationStatement.Type is CSharp.PrimitiveType)
            //{
            //    CSharp.VariableInitializer v = variableDeclarationStatement.Variables.ElementAt(0);
            //    if (v.Initializer is CSharp.PrimitiveExpression)
            //    {
            //        CSharp.PrimitiveExpression p = v.Initializer as CSharp.PrimitiveExpression;
            //        if (p.Value is string)
            //        {
            //            v.Initializer = new CSharp.ObjectCreateExpression(new CSharp.SimpleType("String"), new CSharp.PrimitiveExpression(p.Value));
            //        }
            //    }
            //}

            vds.Type = (AstType)variableDeclarationStatement.Type.AcceptVisitor(this, data);
            ConvertNodes(variableDeclarationStatement.Variables, vds.Variables);
            if (objectCreation)
            {
                vds.Variables.ElementAt(0).NameToken = new Identifier(vds.Variables.ElementAt(0).Name, TextLocation.Empty);
                vds.Type = new PtrType((AstType)vds.Type.Clone());
            }
            else if (arrayCreation)
            {
                SimpleType t = new SimpleType("Array");
                t.TypeArguments.Add((AstType)vds.Type.Clone());

                VariableInitializer vinit = vds.Variables.ElementAt(0);
                vinit.NameToken = new Identifier(vinit.Name, TextLocation.Empty);
                if (vinit.Initializer is ArrayCreateExpression)
                {
                    var obj = new ObjectCreateExpression((AstType)t.Clone());
                    foreach (var n in (vinit.Initializer as ArrayCreateExpression).Arguments)
                    {
                        obj.Arguments.Add(n.Clone());
                    }
                    vinit.Initializer = obj;
                }
                vds.Type = new PtrType((AstType)t.Clone());
            }
            Cache.AddMethodVariableDeclaration(currentMethod, vds);
            return EndNode(variableDeclarationStatement, vds);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitWhileStatement(CSharp.WhileStatement whileStatement, object data)
        {
            WhileStatement whiles = new WhileStatement();
            whiles.Condition = (Expression)whileStatement.Condition.AcceptVisitor(this, data);
            whiles.EmbeddedStatement = (Statement)whileStatement.EmbeddedStatement.AcceptVisitor(this, data);
            return EndNode(whileStatement, whiles);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitYieldBreakStatement(CSharp.YieldBreakStatement yieldBreakStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitYieldReturnStatement(CSharp.YieldReturnStatement yieldReturnStatement, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitAccessor(CSharp.Accessor accessor, object data)
        {
            var method = new CSharp.MethodDeclaration();
            foreach (CSharp.CSharpModifierToken mt in (accessor.Parent as CSharp.PropertyDeclaration).ModifierTokens)
                method.AddChild((CSharp.CSharpModifierToken)mt.Clone(), CSharp.MethodDeclaration.ModifierRole);

            method.PrivateImplementationType = (CSharp.AstType)(accessor.Parent as CSharp.PropertyDeclaration).PrivateImplementationType.Clone();

            string acc = "";
            if (accessor.Role == CSharp.PropertyDeclaration.GetterRole)
            {
                acc = "get";
            }
            else if (accessor.Role == CSharp.PropertyDeclaration.SetterRole)
            {
                acc = "set";
            }
            else if (accessor.Role == CSharp.CustomEventDeclaration.AddAccessorRole)
            {
                throw new NotImplementedException();
            }
            else if (accessor.Role == CSharp.CustomEventDeclaration.RemoveAccessorRole)
            {
                throw new NotImplementedException();
            }

            //Create method declaration node
            bool isEmptyProperty = false;
            string propName = (accessor.Parent as CSharp.PropertyDeclaration).Name;
            method.NameToken = CSharp.Identifier.Create(acc + propName);
            CSharp.AstType returnType = (CSharp.AstType)(accessor.Parent as CSharp.PropertyDeclaration).ReturnType;
            method.Body = (CSharp.BlockStatement)accessor.Body.Clone();
            isEmptyProperty = !method.Body.Statements.Any();

            if (acc == "get")
            {
                method.ReturnType = returnType.Clone();
                if (isEmptyProperty)
                {
                    string varName = propName + "_var";
                    CSharp.BlockStatement blck = new CSharp.BlockStatement();
                    blck.AddChild(new CSharp.ReturnStatement(
                            new CSharp.MemberReferenceExpression(
                                new CSharp.ThisReferenceExpression(), varName)), CSharp.BlockStatement.StatementRole);
                    method.Body = blck;

                    Cache.AddAuxVariable((AstType)returnType.AcceptVisitor(this, data).Clone(), varName);
                }
            }
            else if (acc == "set")
            {
                method.ReturnType = new CSharp.PrimitiveType("void");
                CSharp.ParameterDeclaration pd = new CSharp.ParameterDeclaration(returnType.Clone(), "value");
                method.AddChild(pd, CSharp.MethodDeclaration.Roles.Parameter);

                if (isEmptyProperty)
                {
                    string varName = propName + "_var";
                    CSharp.BlockStatement blck = new CSharp.BlockStatement();
                    blck.AddChild(new CSharp.ExpressionStatement(
                        new CSharp.AssignmentExpression(
                            new CSharp.MemberReferenceExpression(
                                new CSharp.ThisReferenceExpression(), varName), new CSharp.IdentifierExpression("value"))), CSharp.BlockStatement.StatementRole);
                    method.Body = blck;

                    Cache.AddAuxVariable((AstType)returnType.AcceptVisitor(this, data).Clone(), varName);
                }
            }
            else
                throw new NotImplementedException();

            //End method declaration

            //CONVERT TO CPP METHOD        
            var cppMethod = method.AcceptVisitor(this, data);
            return EndNode(accessor, cppMethod);
        }



        AstNode CSharp.IAstVisitor<object, AstNode>.VisitConstructorDeclaration(CSharp.ConstructorDeclaration constructorDeclaration, object data)
        {
            currentMethod = constructorDeclaration.Name;
            var result = new ConstructorDeclaration();

            ConvertNodes(constructorDeclaration.Attributes, result.Attributes);
            ConvertNodes(constructorDeclaration.ModifierTokens, result.ModifierTokens);
            ConvertNodes(constructorDeclaration.Parameters, result.Parameters);
            result.IdentifierToken = (Identifier)constructorDeclaration.IdentifierToken.AcceptVisitor(this, data);
            result.Name = constructorDeclaration.Name;
            result.Body = (BlockStatement)constructorDeclaration.Body.AcceptVisitor(this, data);

            //TODO: C++ WILL NOT COMPILE C# INITIALIZERS
            if (!constructorDeclaration.Initializer.IsNull)
                result.Initializer = (ConstructorInitializer)constructorDeclaration.Initializer.AcceptVisitor(this, data);

            HeaderConstructorDeclaration hc = new HeaderConstructorDeclaration();
            Resolver.GetHeaderNode(result, hc);
            Cache.AddHeaderNode(hc);

            return EndNode(constructorDeclaration, result);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitConstructorInitializer(CSharp.ConstructorInitializer constructorInitializer, object data)
        {
            var cinit = new ConstructorInitializer();
            if (constructorInitializer.ConstructorInitializerType == CSharp.ConstructorInitializerType.Base)//BASE
            {
                //cinit.Base = (ConstructorInitializerType)constructorInitializer.ConstructorInitializerType;
            }
            else //THIS

                ConvertNodes(constructorInitializer.Arguments, cinit.Arguments);
            return EndNode(constructorInitializer, cinit);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitDestructorDeclaration(CSharp.DestructorDeclaration destructorDeclaration, object data)
        {
            currentMethod = destructorDeclaration.Name;
            var result = new DestructorDeclaration();

            ConvertNodes(destructorDeclaration.Attributes, result.Attributes);
            ConvertNodes(destructorDeclaration.ModifierTokens, result.ModifierTokens);
            result.Body = (BlockStatement)destructorDeclaration.Body.AcceptVisitor(this, data);

            var hd = new HeaderDestructorDeclaration();
            Resolver.GetHeaderNode(result, hd);
            Cache.AddHeaderNode(hd);
            return EndNode(destructorDeclaration, result);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitEnumMemberDeclaration(CSharp.EnumMemberDeclaration enumMemberDeclaration, object data)
        {
            var enumMember = new EnumMemberDeclaration();
            enumMember.Initializer = (Expression)enumMemberDeclaration.Initializer.AcceptVisitor(this, data);
            enumMember.NameToken = (Identifier)enumMemberDeclaration.NameToken.AcceptVisitor(this, data);

            return EndNode(enumMemberDeclaration, enumMember);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitEventDeclaration(CSharp.EventDeclaration eventDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCustomEventDeclaration(CSharp.CustomEventDeclaration customEventDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitFieldDeclaration(CSharp.FieldDeclaration fieldDeclaration, object data)
        {
            var decl = new FieldDeclaration();

            decl.ReturnType = (AstType)fieldDeclaration.ReturnType.AcceptVisitor(this, data);
            decl.Modifiers = ConvertModifiers(fieldDeclaration.Modifiers, fieldDeclaration);
            ConvertNodes(fieldDeclaration.Attributes, decl.Attributes);
            ConvertNodes(fieldDeclaration.Variables, decl.Variables);

            if (!fieldDeclaration.HasModifier(CSharp.Modifiers.Static) && !currentType.TypeParameters.Any())
            {
                foreach (VariableInitializer vi in decl.Variables)
                {
                    if (!vi.Initializer.IsNull)
                    {
                        Statement st = new ExpressionStatement(
                            new AssignmentExpression(
                                new IdentifierExpression(vi.Name), vi.Initializer.Clone()));

                        Cache.AddConstructorStatement(st);
                    }
                }

                //Reset the variable initializer befor add to header ndoes
                for (int i = 0; i < fieldDeclaration.Variables.Count; i++)
                {
                    VariableInitializer vi = decl.Variables.ElementAt(i);
                    decl.Variables.Remove(vi);
                    vi = new VariableInitializer(vi.Name);
                    decl.Variables.Add(vi);
                }
            }

            Cache.AddField(currentType.Name, decl);
            var hf = new HeaderFieldDeclaration();
            Resolver.GetHeaderNode(decl, hf);
            Cache.AddHeaderNode(hf);
            return EndNode(fieldDeclaration, decl);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitIndexerDeclaration(CSharp.IndexerDeclaration indexerDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitMethodDeclaration(CSharp.MethodDeclaration methodDeclaration, object data)
        {
            Cache.ClearParametersAndFieldsDeclarations();
            currentMethod = methodDeclaration.Name;

            if (isInterface || methodDeclaration.HasModifier(CSharp.Modifiers.Abstract))
            {
                var res = new HeaderAbstractMethodDeclaration();
                res.Name = methodDeclaration.Name;
                res.NameToken = (Identifier)methodDeclaration.NameToken.AcceptVisitor(this, data);
                res.ReturnType = (AstType)methodDeclaration.ReturnType.AcceptVisitor(this, data);

                ConvertNodes(methodDeclaration.Parameters, res.Parameters);
                ConvertNodes(methodDeclaration.TypeParameters, res.TypeParameters);

                if (!res.HasModifier(Modifiers.Public))
                    res.ModifierTokens.Add(new CppModifierToken(TextLocation.Empty, Modifiers.Public));

                //TODO: NOT SURE IF IT IS NECESSARY
                if (res.PrivateImplementationType is PtrType)
                    res.PrivateImplementationType = (AstType)(res.PrivateImplementationType as PtrType).Target.Clone();

                if (res.PrivateImplementationType != AstType.Null)
                    Cache.AddPrivateImplementation(res.PrivateImplementationType, res);

                if (Resolver.IsTemplateType(res.ReturnType))
                {
                    InvocationExpression ic = new InvocationExpression(new IdentifierExpression("TypeDecl"), new IdentifierExpression(Resolver.GetTypeName(res.ReturnType)));
                    res.ReturnType = new ExpressionType(ic);
                }

                //END
                Cache.AddHeaderNode(res);
                return EndNode(methodDeclaration, res);

            }

            var result = new MethodDeclaration();

            ConvertNodes(methodDeclaration.Attributes.Where(section => section.AttributeTarget != "return"), result.Attributes);
            ConvertNodes(methodDeclaration.ModifierTokens, result.ModifierTokens);
            result.Name = methodDeclaration.Name;

            ConvertNodes(methodDeclaration.Parameters, result.Parameters);
            ConvertNodes(methodDeclaration.TypeParameters, result.TypeParameters);

            result.ReturnType = (AstType)methodDeclaration.ReturnType.AcceptVisitor(this, data);
            result.Body = (BlockStatement)methodDeclaration.Body.AcceptVisitor(this, data);
            result.PrivateImplementationType = (AstType)methodDeclaration.PrivateImplementationType.AcceptVisitor(this, data);

            if (currentType != null)
                result.AddChild((Identifier)currentType.NameToken.AcceptVisitor(this, data), MethodDeclaration.TypeRole);

            if (result.PrivateImplementationType is PtrType)
                result.PrivateImplementationType = (AstType)(result.PrivateImplementationType as PtrType).Target.Clone();

            if (result.PrivateImplementationType != AstType.Null)
                Cache.AddPrivateImplementation(result.PrivateImplementationType, result);

            var hm = new HeaderMethodDeclaration();
            Resolver.GetHeaderNode(result, hm);

            if (methodDeclaration.Name == "Main")
            {
                CSharp.NamespaceDeclaration nms = methodDeclaration.Parent.Parent as CSharp.NamespaceDeclaration;
                hm.Namespace = nms == null ? String.Empty : nms.Name;
            }
            Cache.AddHeaderNode(hm);

            return EndNode(methodDeclaration, result);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitOperatorDeclaration(CSharp.OperatorDeclaration operatorDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitParameterDeclaration(CSharp.ParameterDeclaration parameterDeclaration, object data)
        {
            var param = new ParameterDeclaration();

            ConvertNodes(parameterDeclaration.Attributes, param.Attributes);
            param.ParameterModifier = (ParameterModifier)parameterDeclaration.ParameterModifier;
            param.Type = (AstType)parameterDeclaration.Type.AcceptVisitor(this, data);
            param.NameToken = (Identifier)parameterDeclaration.NameToken.AcceptVisitor(this, data);
            if (param.NameToken is ComposedIdentifier)
            {
                CSharp.MethodDeclaration m = null;
                if (Resolver.IsChildOf(parameterDeclaration, typeof(CSharp.MethodDeclaration)))
                    m = (CSharp.MethodDeclaration)Resolver.GetParentOf(parameterDeclaration, typeof(CSharp.MethodDeclaration));

                if (m != null)
                    if (m.Name == "Main")
                        goto End;

                SimpleType s = new SimpleType("Array");
                s.TypeArguments.Add((AstType)param.Type.Clone());
                param.Type = new PtrType(s);

                param.NameToken = (Identifier)(param.NameToken as ComposedIdentifier).BaseIdentifier.Clone();
            }
        End:
            param.DefaultExpression = (Expression)parameterDeclaration.DefaultExpression.AcceptVisitor(this, data);

            Cache.AddParameterDeclaration(currentMethod, param);
            return EndNode(parameterDeclaration, param);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitPropertyDeclaration(CSharp.PropertyDeclaration propertyDeclaration, object data)
        {
            if (currentType != null) //If currentType is null, the property is on the treeView
                Cache.AddProperty(propertyDeclaration.Name, currentType.Name);

            PropertyDeclaration pdecl = new PropertyDeclaration();
            pdecl.Getter = (MethodDeclaration)propertyDeclaration.Getter.AcceptVisitor(this, data);
            pdecl.Setter = (MethodDeclaration)propertyDeclaration.Setter.AcceptVisitor(this, data);
            pdecl.NameToken = (Identifier)propertyDeclaration.NameToken.AcceptVisitor(this, data);
            pdecl.PrivateImplementationType = (AstType)propertyDeclaration.PrivateImplementationType.AcceptVisitor(this, data);
            pdecl.ReturnType = (AstType)propertyDeclaration.ReturnType.AcceptVisitor(this, data);
            pdecl.Name = propertyDeclaration.Name;

            ConvertNodes(propertyDeclaration.ModifierTokens, pdecl.ModifierTokens);
            return EndNode(propertyDeclaration, pdecl);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitVariableInitializer(CSharp.VariableInitializer variableInitializer, object data)
        {
            var vi = new VariableInitializer();

            vi.Initializer = (Expression)variableInitializer.Initializer.AcceptVisitor(this, data);

            if (vi.Initializer is MemberReferenceExpression)
            {
                MemberReferenceExpression mre = vi.Initializer as MemberReferenceExpression;
                if (Resolver.IsPropertyCall(mre, currentType.Name))
                {
                    //GET
                    InvocationExpression m = new InvocationExpression(
                        new MemberReferenceExpression(mre.Target.Clone(), "get" + mre.MemberName), new Expression[1] { new EmptyExpression() });
                    vi.Initializer = m;
                }
            }

            vi.Name = variableInitializer.Name;
            vi.NameToken = (Identifier)variableInitializer.NameToken.AcceptVisitor(this, data);

            return EndNode(variableInitializer, vi);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitFixedFieldDeclaration(CSharp.FixedFieldDeclaration fixedFieldDeclaration, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitFixedVariableInitializer(CSharp.FixedVariableInitializer fixedVariableInitializer, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCompilationUnit(CSharp.CompilationUnit compilationUnit, object data)
        {
            var unit = new CompilationUnit();

            foreach (var node in compilationUnit.Children)
                unit.AddChild(node.AcceptVisitor(this, null), CompilationUnit.MemberRole);

            return EndNode(compilationUnit, unit);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitSimpleType(CSharp.SimpleType simpleType, object data)
        {
            string id = simpleType.Identifier;
            bool isPtr = true;

            if (Resolver.IsChildOf(simpleType, typeof(CSharp.UsingDeclaration)) && !Resolver.IsChildOf(simpleType, typeof(CSharp.MemberType))
                && !Resolver.IsChildOf(simpleType, typeof(CSharp.TypeParameterDeclaration)))
            {
                id = Resolver.GetCppName(simpleType.Identifier);
                Resolver.AddInclude(simpleType.Identifier);
                isPtr = false;
            }

            var type = new SimpleType(id);
            ConvertNodes(simpleType.TypeArguments, type.TypeArguments);
            if (simpleType.TypeArguments.Any())
            {
                type.Identifier += "_T";
                Resolver.AddVistedType(type, type.Identifier);
            }

            if (!Resolver.IsChildOf(simpleType, typeof(CSharp.UsingDeclaration)))
            {
                //Add the visited type to the resolver in order to include it after
                //Also this call adds the type to the include list for detecting forward declarations
                //If its parent is null, is better to ignore :)
                //Ignore the type if is the current type declaration
                if (simpleType.Parent != null && (currentType == null ? "N/A" : currentType.Name) != simpleType.Identifier)
                    Resolver.AddVistedType(type, type.Identifier);

                if (simpleType.Annotations.Count() > 0)
                    Resolver.AddSymbol(id, simpleType.Annotations.ElementAt(0) as TypeReference);


                //If the type is in the Visual Tree, the parent is null. 
                //If its parent is a TypeReferenceExpression it is like Console::ReadLine          
                //If the Role is BaseTypeRole it means that it is a inherited class (i.e. MyClass : public MyInheritedClass)
                if (simpleType.Parent == null || !isPtr || Resolver.IsChildOf(simpleType, typeof(CSharp.TypeReferenceExpression))
                    || simpleType.Role == CSharp.TypeDeclaration.BaseTypeRole)
                    return EndNode(simpleType, type);

                //The type is like MyTemplate<MyType> 
                //Maybe we should not check the TypeParameter ?
                if (simpleType.Role == CSharp.SimpleType.Roles.TypeArgument || simpleType.Role == CSharp.SimpleType.Roles.TypeParameter)
                    return EndNode(simpleType, type);

                var ptrType = new PtrType(type);
                return EndNode(simpleType, ptrType);
            }
            else
            {
                //if (simpleType.Role == CSharp.SimpleType.Roles.TypeArgument || simpleType.Role == CSharp.SimpleType.Roles.TypeParameter)
                //Cache.AddExcludedType(type.Identifier);
                return EndNode(simpleType, type);
            }
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitMemberType(CSharp.MemberType memberType, object data)
        {
            AstType target = null;

            //if (memberType.Target is CSharp.SimpleType && ((CSharp.SimpleType)(memberType.Target)).Identifier.Equals("global", StringComparison.Ordinal))
            //    target = new PrimitiveType("Global");
            //else
            //    target = (AstType)memberType.Target.AcceptVisitor(this, data);

            target = (AstType)memberType.Target.AcceptVisitor(this, data);

            var type = new QualifiedType(target, new Identifier(memberType.MemberName, TextLocation.Empty));
            ConvertNodes(memberType.TypeArguments, type.TypeArguments);

            return EndNode(memberType, type);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitComposedType(CSharp.ComposedType composedType, object data)
        {
            //If there is ArraySpecifier, get it and return the simpleType or primitiveType
            if (composedType.ArraySpecifiers.Any())
                Cache.AddRangeArraySpecifiers(composedType.ArraySpecifiers);

            if (composedType.HasNullableSpecifier)
            {
                ComposedType ctype = new ComposedType();
                ctype.BaseType = (AstType)composedType.BaseType.AcceptVisitor(this, data);
                ctype.HasNullableSpecifier = composedType.HasNullableSpecifier;
                return EndNode(composedType, ctype);
            }
            return composedType.BaseType.AcceptVisitor(this, data);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitArraySpecifier(CSharp.ArraySpecifier arraySpecifier, object data)
        {
            return EndNode(arraySpecifier, new ArraySpecifier(arraySpecifier.Dimensions));
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitPrimitiveType(CSharp.PrimitiveType primitiveType, object data)
        {
            string typeName;

            switch (primitiveType.Keyword.ToLower())
            {
                case "sbyte":
                    typeName = "short";
                    break;
                case "byte":
                    typeName = "char";
                    break;
                case "decimal":
                    typeName = "float";
                    break;
                case "double":
                    typeName = "float";
                    break;
                case "object":
                    return EndNode(primitiveType, new PtrType(new SimpleType("Object")));
                case "string":
                    if (primitiveType.Role == CSharp.SimpleType.Roles.TypeArgument || primitiveType.Role == CSharp.SimpleType.Roles.TypeParameter)
                        return EndNode(primitiveType, new SimpleType("String"));
                    return EndNode(primitiveType, new PtrType(new SimpleType("String")));
                default:
                    typeName = primitiveType.Keyword;
                    break;
            }
            return EndNode(primitiveType, new PrimitiveType(typeName));
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitComment(CSharp.Comment comment, object data)
        {
            CommentType cmntType;
            switch (comment.CommentType)
            {
                case CSharp.CommentType.Documentation:
                    cmntType = CommentType.Documentation;
                    break;
                case CSharp.CommentType.InactiveCode:
                    cmntType = CommentType.InactiveCode;
                    break;
                case CSharp.CommentType.MultiLine:
                    cmntType = CommentType.MultiLine;
                    break;
                case CSharp.CommentType.SingleLine:
                    cmntType = CommentType.SingleLine;
                    break;
                default:
                    throw new Exception("Invalid comment type");
            }
            Comment cmnt = new Comment(comment.Content, cmntType);
            return cmnt;
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitPreProcessorDirective(CSharp.PreProcessorDirective preProcessorDirective, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitTypeParameterDeclaration(CSharp.TypeParameterDeclaration typeParameterDeclaration, object data)
        {
            TypeParameterDeclaration t = new TypeParameterDeclaration();

            t.Name = typeParameterDeclaration.Name;
            t.NameToken = (Identifier)typeParameterDeclaration.NameToken.AcceptVisitor(this, data);
            Cache.AddTemplateType(t.Name);
            return EndNode(typeParameterDeclaration, t);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitConstraint(CSharp.Constraint constraint, object data)
        {
            throw new NotImplementedException();
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitCSharpTokenNode(CSharp.CSharpTokenNode cSharpTokenNode, object data)
        {
            var mod = cSharpTokenNode as CSharp.CSharpModifierToken;
            if (mod != null)
            {
                var convertedModifiers = ConvertModifiers(mod.Modifier, mod.Parent);
                CppModifierToken token = null;
                if (convertedModifiers != Modifiers.None)
                {
                    token = new CppModifierToken(TextLocation.Empty, convertedModifiers);
                    return EndNode(cSharpTokenNode, token);
                }
                return EndNode(cSharpTokenNode, token);
            }
            else
            {
                throw new NotSupportedException("Should never visit individual tokens");
            }
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitIdentifier(CSharp.Identifier identifier, object data)
        {
            if (Cache.ArraySpecifiersAny())
            {
                List<CSharp.ArraySpecifier> arraySpecifiers = Cache.GetArraySpecifiers();
                var compIdent = new ComposedIdentifier(identifier.Name, TextLocation.Empty);
                ConvertNodes(arraySpecifiers, compIdent.ArraySpecifiers);
                arraySpecifiers.Clear();

                compIdent.BaseIdentifier = (Identifier)identifier.AcceptVisitor(this, data);

                return compIdent;
            }
            var ident = new Identifier(identifier.Name, identifier.StartLocation);

            return EndNode(identifier, ident);
        }

        AstNode CSharp.IAstVisitor<object, AstNode>.VisitPatternPlaceholder(CSharp.AstNode placeholder, PatternMatching.Pattern pattern, object data)
        {
            throw new NotImplementedException();
        }

        void ConvertNodes<T>(IEnumerable<CSharp.AstNode> nodes, Cpp.AstNodeCollection<T> result) where T : Cpp.AstNode
        {
            foreach (var node in nodes)
            {
                T n = (T)node.AcceptVisitor(this, null);
                if (n != null)
                    result.Add(n);
            }
        }

        T EndNode<T>(CSharp.AstNode node, T result) where T : Cpp.AstNode
        {
            if (result != null)
            {
                CopyAnnotations(node, result);
            }

            return result;
        }

        void CopyAnnotations<T>(CSharp.AstNode node, T result) where T : Cpp.AstNode
        {
            foreach (var ann in node.Annotations)
                result.AddAnnotation(ann);
        }

        Modifiers ConvertModifiers(CSharp.Modifiers modifier, CSharp.AstNode container)
        {
            if ((modifier & CSharp.Modifiers.Any) == CSharp.Modifiers.Any)
                return Modifiers.Any;

            var mod = Modifiers.None;

            if ((modifier & CSharp.Modifiers.Static) == CSharp.Modifiers.Static)
                mod |= Modifiers.Static;

            if ((modifier & CSharp.Modifiers.Public) == CSharp.Modifiers.Public)
                mod |= Modifiers.Public;
            if ((modifier & CSharp.Modifiers.Protected) == CSharp.Modifiers.Protected)
                mod |= Modifiers.Protected;
            if ((modifier & CSharp.Modifiers.Internal) == CSharp.Modifiers.Internal)
                mod |= Modifiers.Public;
            if ((modifier & CSharp.Modifiers.Private) == CSharp.Modifiers.Private)
                mod |= Modifiers.Private;

            if ((modifier & CSharp.Modifiers.Abstract) == CSharp.Modifiers.Abstract)
            {
                if (container is CSharp.TypeDeclaration)
                    mod |= Modifiers.Abstract;//MUST INHERIT
                else
                    mod |= Modifiers.Virtual;//MUST OVERRIDE
            }

            if ((modifier & CSharp.Modifiers.Override) == CSharp.Modifiers.Override)
                mod |= Modifiers.Virtual;//CPP OVERRIDE KEYWORD IS NOT ALLOWED BY ALL THE COMPILERS
            if ((modifier & CSharp.Modifiers.Virtual) == CSharp.Modifiers.Virtual)
                mod |= Modifiers.Virtual;

            return mod;
        }

        bool HasAttribute(CSharp.AstNodeCollection<CSharp.AttributeSection> attributes, string name, out CSharp.Attribute foundAttribute)
        {
            foreach (var attr in attributes.SelectMany(a => a.Attributes))
            {
                if (provider.GetTypeNameForAttribute(attr) == name)
                {
                    foundAttribute = attr;
                    return true;
                }
            }
            foundAttribute = null;
            return false;
        }
    }
}
