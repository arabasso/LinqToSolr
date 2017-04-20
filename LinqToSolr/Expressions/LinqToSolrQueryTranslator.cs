﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using LinqToSolr.Data;
using LinqToSolr.Services;
using Newtonsoft.Json;

namespace LinqToSolr.Expressions
{
    internal class LinqToSolrQueryTranslator: ExpressionVisitor
    {
        private StringBuilder sb;
        private bool _inRangeQuery;
        private ILinqToSolrService _service;
        private readonly IDictionary<string, string> solrFields;
        private bool _isRedudant;
        private ICollection<string> _sortings;
        private Type _elementType;
        internal LinqToSolrQueryTranslator(ILinqToSolrService query)
        {
            _service = query;
            _elementType = GetElementType(_service.ElementType);
            solrFields = GetFields();
            _sortings = new List<string>();
        }
        internal LinqToSolrQueryTranslator(ILinqToSolrService query, Type elementType)
        {
            _service = query;
            _elementType = GetElementType(elementType);
            solrFields = GetFields();
            _sortings = new List<string>();
            _elementType = elementType;
        }
        internal Dictionary<string, string> GetFields()
        {
            var dic = new Dictionary<string, string>();
            var props = _elementType.GetProperties();
            foreach (var p in props)
            {
                var dataMemberAttribute = p.GetCustomAttribute<JsonPropertyAttribute>();

                var fieldName = !string.IsNullOrEmpty(dataMemberAttribute?.PropertyName)
                       ? dataMemberAttribute.PropertyName
                       : p.Name;
                dic.Add(p.Name, fieldName);

            }

            return dic;

        }

        private Type GetElementType(Type type)
        {

            if (type.Name == "IGrouping`2")
            {
                return type.GetGenericArguments()[1];
            }
            return type;
        }

        internal string Translate(Expression expression)
        {
            _isRedudant = true;
            sb = new StringBuilder();
            Visit(expression);
            return sb.ToString().Replace(_elementType.Name, "");
        }


        private static Expression StripQuotes(Expression e)
        {
            while (e.NodeType == ExpressionType.Quote)
            {
                e = ((UnaryExpression)e).Operand;
            }
            return e;
        }


        protected override Expression VisitMethodCall(MethodCallExpression m)
        {
            if (m.Method.DeclaringType == typeof(Queryable) && (m.Method.Name == "Where" || m.Method.Name == "First" || m.Method.Name == "FirstOrDefault"))
            {
                var lambda = (LambdaExpression)StripQuotes(m.Arguments[1]);

                var solrQueryTranslator = new LinqToSolrQueryTranslator(_service);
                var fq = solrQueryTranslator.Translate(lambda.Body);
                sb.AppendFormat("&fq={0}", fq);

                var arr = StripQuotes(m.Arguments[0]);
                Visit(arr);
                return m;
            }
            if (m.Method.DeclaringType == typeof(Queryable) && (m.Method.Name == "Take"))
            {
                var takeNumber = (int)((ConstantExpression)m.Arguments[1]).Value;
                _service.Configuration.Take = takeNumber;
                Visit(m.Arguments[0]);
                return m;
            }
            if (m.Method.DeclaringType == typeof(Queryable) && (m.Method.Name == "Skip"))
            {
                var skipNumber = (int)((ConstantExpression)m.Arguments[1]).Value;
                _service.Configuration.Start = skipNumber;
                Visit(m.Arguments[0]);
                return m;
            }


            if (m.Method.DeclaringType == typeof(Queryable) && (m.Method.Name == "OrderBy" || m.Method.Name == "ThenBy"))
            {
                var lambda = (LambdaExpression)StripQuotes(m.Arguments[1]);

                _service.CurrentQuery.AddSorting(lambda.Body, SolrSortTypes.Asc);

                Visit(m.Arguments[0]);

                return m;
            }

            if (m.Method.DeclaringType == typeof(Queryable) && (m.Method.Name == "OrderByDescending" || m.Method.Name == "ThenByDescending"))
            {
                var lambda = (LambdaExpression)StripQuotes(m.Arguments[1]);
                _service.CurrentQuery.AddSorting(lambda.Body, SolrSortTypes.Desc);

                Visit(m.Arguments[0]);

                return m;
            }



            if (m.Method.DeclaringType == typeof(Queryable) && (m.Method.Name == "Select"))
            {
                _service.CurrentQuery.Select = new SolrSelect(StripQuotes(m.Arguments[1]));
                //    Visit(m.Arguments[0]);

                return m;
            }


            if (m.Method.Name == "Contains")
            {
                if (m.Method.DeclaringType == typeof(string))
                {
                    var str = string.Format("*{0}*", ((ConstantExpression)StripQuotes(m.Arguments[0])).Value);


                    Visit(BinaryExpression.Equal(m.Object, ConstantExpression.Constant(str)));

                    return m;
                }
                else
                {
                    var arr = (ConstantExpression)StripQuotes(m.Arguments[0]);
                    var lambda = (MemberExpression)StripQuotes(m.Arguments[1]);
                    Visit(lambda);
                    Visit(arr);
                    var solrQueryTranslator = new LinqToSolrQueryTranslator(_service);
                    var fieldName = solrQueryTranslator.Translate(arr);

                    return m;
                }
            }

            if (m.Method.Name == "StartsWith")
            {
                if (m.Method.DeclaringType == typeof(string))
                {
                    var str = string.Format("{0}*", ((ConstantExpression)StripQuotes(m.Arguments[0])).Value);
                    Visit(BinaryExpression.Equal(m.Object, ConstantExpression.Constant(str)));

                    return m;
                }
            }
            if (m.Method.Name == "EndsWith")
            {
                if (m.Method.DeclaringType == typeof(string))
                {
                    var str = string.Format("*{0}", ((ConstantExpression)StripQuotes(m.Arguments[0])).Value);
                    Visit(BinaryExpression.Equal(m.Object, ConstantExpression.Constant(str)));

                    return m;

                }
            }

            if (m.Method.Name == "GroupBy")
            {

                _service.CurrentQuery.IsGroupEnabled = true;
                var arr = StripQuotes(m.Arguments[1]);
                var solrQueryTranslator = new LinqToSolrQueryTranslator(_service, ((MemberExpression)((LambdaExpression)arr).Body).Member.ReflectedType);
                _service.CurrentQuery.GroupFields.Add(solrQueryTranslator.Translate(arr));
                Visit(m.Arguments[0]);

                return m;

                //throw new Exception("The method 'GroupBy' is not supported in Solr. For native FACETS support use SolrQuaryableExtensions.GroupBySolr instead.");
            }

            throw new NotSupportedException(string.Format("The method '{0}' is not supported", m.Method.Name));
        }

        protected override Expression VisitUnary(UnaryExpression u)
        {
            switch (u.NodeType)
            {
                case ExpressionType.Not:
                    sb.Append("-");
                    Visit(u.Operand);
                    break;

                case ExpressionType.Convert:
                    Visit(u.Operand);
                    break;
                default:
                    throw new NotSupportedException(string.Format("The unary operator '{0}' is not supported", u.NodeType));
            }

            return u;
        }


        protected override Expression VisitBinary(BinaryExpression b)
        {
            _isRedudant = false;
            sb.Append("(");

            if (b.Left is ConstantExpression)
            {
                throw new InvalidExpressionException("Failed to parse expression. Ensure the Solr fields are always come in the left part of comparison.");
            }
            Visit(b.Left);

            switch (b.NodeType)
            {
                case ExpressionType.And:
                case ExpressionType.AndAlso:
                    sb.Append(" AND ");
                    break;

                case ExpressionType.Or:
                case ExpressionType.OrElse:
                    sb.Append(" OR ");
                    break;

                case ExpressionType.Equal:
                    sb.Append(":");
                    break;
                case ExpressionType.NotEqual:
                    sb.Append(":=");
                    break;
                case ExpressionType.GreaterThanOrEqual:
                    sb.Append(":[");
                    _inRangeQuery = true;
                    break;

                case ExpressionType.LessThanOrEqual:
                    sb.Append(":[*");
                    _inRangeQuery = true;
                    break;

                default:

                    throw new NotSupportedException(string.Format("The binary operator '{0}' is not supported",
                        b.NodeType));
            }

            Visit(b.Right);


            if (b.NodeType != ExpressionType.Equal &&
                b.NodeType != ExpressionType.NotEqual &&
                b.Right is MemberExpression &&
                (b.Left is BinaryExpression || b.Left.NodeType == ExpressionType.Call))
            {
                if (((MemberExpression)b.Right).Type == typeof(bool))
                {
                    sb.Append(":");
                    sb.Append(" True");
                }
            }

            sb.Append(")");

            return b;
        }


        protected override Expression VisitConstant(ConstantExpression c)
        {
            var q = c.Value as IQueryable;

            if (q != null)
            {
                sb.Append(_elementType.Name);
            }
            else
            {
                //handle in range query
                if (_inRangeQuery)
                {
                    if (sb[sb.Length - 1] == '*')
                    {
                        sb.Append(" TO  ");
                        AppendConstValue(c.Value);
                    }
                    else
                    {
                        AppendConstValue(c.Value);
                        sb.Append(" TO *");
                    }
                    sb.Append("]");
                    _inRangeQuery = false;
                }
                else
                {
                    AppendConstValue(c.Value);
                }
            }

            return c;
        }

        private void AppendConstValue(object val)
        {
            var isArray = val.GetType().GetInterface("IEnumerable`1") != null;
            //Set date format of Solr 1995-12-31T23:59:59.999Z
            if (val.GetType() == typeof(DateTime))
            {
                sb.Append(((DateTime)val).ToString("yyyy-MM-ddThh:mm:ss.fffZ"));
            }
            else if (!(val is string) && isArray)
            {
                var array = (IEnumerable)val;
                var arrstring = string.Join(" OR ", array.Cast<object>().Select(x => string.Format("\"{0}\"", x)));
                sb.AppendFormat(": ({0})", arrstring);

            }
            else
            {
                if (val.ToString().Contains(" ") && !val.ToString().Contains("*"))
                {
                    sb.Append(string.Format("\"{0}\"", val));

                }
                else
                {
                    sb.Append(val);

                }
            }
        }

        protected override MemberAssignment VisitMemberAssignment(MemberAssignment node)
        {
            return base.VisitMemberAssignment(node);
        }

        protected override Expression VisitMember(MemberExpression m)
        {
            if (m.Expression != null && m.Expression.NodeType == ExpressionType.Parameter)
            {

                var fieldName = solrFields[m.Member.Name];
                sb.Append(fieldName);
                return m;
            }
            if (m.Expression != null && (m.Expression.NodeType == ExpressionType.Constant || m.Expression.NodeType == ExpressionType.MemberAccess))
            {
                var ce = (ConstantExpression)m.Expression;
                sb.Append(ce.Value);

                return m;
            }

            throw new NotSupportedException(string.Format("The member '{0}' is not supported", m.Member.Name));
        }
    }
}