/*-------------------------------------------------------------------------

  Fastpath.cs
      This class is a port of the class Fastpath.java implemented by
      PostgreSQL Global Development Group

 Copyright (c) 2004, Emiliano Necciari
 Original Code: Copyright (c) 2003, PostgreSQL Global Development Group

 Note: (Francisco Figueiredo Jr.)
      Changed case of method names to conform to .Net names standard.
      Also changed type names to their true names. i.e. int -> Int32

// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
-------------------------------------------------------------------------
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Npgsql;

namespace NpgsqlTypes
{
    /*
     * This class implements the Fastpath api.
     *
     *
     *
     */

    public class Fastpath
    {
        // This maps the functions names to their id's (possible unique just
        // to a connection).
        protected Dictionary<string, int> func = new Dictionary<string, int>();

        protected NpgsqlConnection conn; // our connection
        protected Stream stream; // the network stream

        /*
         * Initialises the fastpath system
         *
         * @param conn BaseConnection to attach to
         * @param stream The network stream to the backend
         */

        public Fastpath(NpgsqlConnection conn, Stream stream)
        {
            this.conn = conn;
            this.stream = stream;
        }

        /*
         * Initialises the fastpath system
         *
         * @param conn BaseConnection to attach to
         * @param stream The network stream to the backend
         */

        public Fastpath(NpgsqlConnection conn)
        {
            this.conn = conn;
            // check if the connection is closed ?
            this.stream = conn.Connector.Stream;
        }

        /*
         * Send a function call to the PostgreSQL backend
         *
         * @param fnid Function id
         * @param resulttype True if the result is an integer, false for other results
         * @param args FastpathArguments to pass to fastpath
         * @return null if no data, Integer if an integer result, or byte[] otherwise
         * @exception NpgsqlException if a database-access error occurs.
         */

        public Object FastpathCall(Int32 fnid, Boolean resulttype, FastpathArg[] args)
        {
            try
            {
                if (conn.BackendProtocolVersion == ProtocolVersion.Version3)
                {
                    return FastpathV3(fnid, resulttype, args);
                }
                else
                {
                    return FastpathV2(fnid, resulttype, args);
                }
            }
            catch (IOException)
            {
                conn.ClearPool();
                throw new NpgsqlException("The Connection is broken.");
            }
        }

        private Object FastpathV3(Int32 fnid, Boolean resulttype, FastpathArg[] args)
        {
            // give  thread safety
            lock (stream)
            {
                // send the function call

                {
                    Int32 l_msgLen = 0;
                    l_msgLen += 16;
                    for (Int32 i = 0; i < args.Length; i++)
                    {
                        l_msgLen += args[i].SendSize();
                    }

                    stream
                        .WriteBytes((Byte)ASCIIBytes.F)
                        .WriteInt32(l_msgLen)
                        .WriteInt32(fnid)
                        .WriteInt16(1)
                        .WriteInt16(1)
                        .WriteInt16((short)args.Length);

                    for (Int32 i = 0; i < args.Length; i++)
                    {
                        args[i].Send(stream);
                    }

                    stream.WriteInt16(1);

                    // This is needed, otherwise data can be lost
                    stream.Flush();
                }

                // Now handle the result

                // Now loop, reading the results
                Object result = null; // our result
                Exception error = null;
                Int32 c;
                Boolean l_endQuery = false;

                while (!l_endQuery)
                {
                    c = (Char) stream.ReadByte();

                    switch (c)
                    {
                        case 'A': // Asynchronous Notify
                            Int32 msglen = PGUtil.ReadInt32(stream);
                            Int32 pid = PGUtil.ReadInt32(stream);
                            String msg = PGUtil.ReadString(stream);
                            PGUtil.ReadString(stream);
                            String param = PGUtil.ReadString(stream);

                            break;
                            //------------------------------
                            // Error message returned
                        case 'E':
                            NpgsqlError e = new NpgsqlError(conn.BackendProtocolVersion, stream);
                            throw new NpgsqlException(e.ToString());

                            //------------------------------
                            // Notice from backend
                        case 'N':
                            Int32 l_nlen = PGUtil.ReadInt32(stream);

                            conn.Connector.FireNotice(new NpgsqlError(conn.BackendProtocolVersion, stream));

                            break;

                        case 'V':
                            Int32 l_msgLen = PGUtil.ReadInt32(stream);
                            Int32 l_valueLen = PGUtil.ReadInt32(stream);

                            if (l_valueLen == -1)
                            {
                                //null value
                            }
                            else if (l_valueLen == 0)
                            {
                                result = new Byte[0];
                            }
                            else
                            {
                                // Return an Integer if
                                if (resulttype)
                                {
                                    result = PGUtil.ReadInt32(stream);
                                }
                                else
                                {
                                    Byte[] buf = new Byte[l_valueLen];

                                    Int32 bytes_from_stream = 0;
                                    Int32 total_bytes_read = 0;
                                    Int32 size = l_valueLen;
                                    do
                                    {
                                        bytes_from_stream = stream.Read(buf, total_bytes_read, size);
                                        total_bytes_read += bytes_from_stream;
                                        size -= bytes_from_stream;
                                    }
                                    while (size > 0);

                                    result = buf;
                                }
                            }
                            break;

                        case 'Z':
                            //TODO: use size better
                            if (PGUtil.ReadInt32(stream) != 5)
                            {
                                throw new NpgsqlException("Received Z");
                            }
                            //TODO: handle transaction status
                            Char l_tStatus = (Char) stream.ReadByte();
                            l_endQuery = true;
                            break;

                        default:
                            throw new NpgsqlException(string.Format("postgresql.fp.protocol received {0}", c));
                    }
                }

                if (error != null)
                {
                    throw error;
                }

                return result;
            }
        }

        private Object FastpathV2(Int32 fnid, Boolean resulttype, FastpathArg[] args)
        {
            // added Oct 7 1998 to give us thread safety
            lock (stream)
            {
                // send the function call

                stream
                    .WriteBytesNullTerminated((byte)ASCIIBytes.F)
                    .WriteInt32(fnid)
                    .WriteInt32(args.Length);

                for (Int32 i = 0; i < args.Length; i++)
                {
                    args[i].Send(stream);
                }

                // This is needed, otherwise data can be lost
                stream.Flush();

                // Now handle the result

                // Now loop, reading the results
                Object result = null; // our result
                String errorMessage = "";
                Int32 c;
                Boolean l_endQuery = false;
                while (!l_endQuery)
                {
                    c = (Char) stream.ReadByte();

                    switch (c)
                    {
                        case 'A': // Asynchronous Notify
                            //TODO: do something with this
                            Int32 pid = PGUtil.ReadInt32(stream);
                            String msg = PGUtil.ReadString(stream);

                            break;

                            //------------------------------
                            // Error message returned
                        case 'E':
                            NpgsqlError e = new NpgsqlError(conn.BackendProtocolVersion, stream);
                            errorMessage += e.Message;
                            break;

                            //------------------------------
                            // Notice from backend
                        case 'N':
                            NpgsqlError notice = new NpgsqlError(conn.BackendProtocolVersion, stream);
                            errorMessage += notice.Message;
                            break;

                        case 'V':
                            Char l_nextChar = (Char) stream.ReadByte();
                            if (l_nextChar == 'G')
                            {
                                Int32 sz = PGUtil.ReadInt32(stream);
                                // Return an Integer if
                                if (resulttype)
                                {
                                    result = PGUtil.ReadInt32(stream);
                                }
                                else
                                {
                                    Byte[] buf = new Byte[sz];

                                    Int32 bytes_from_stream = 0;
                                    Int32 total_bytes_read = 0;
                                    Int32 size = sz;
                                    do
                                    {
                                        bytes_from_stream = stream.Read(buf, total_bytes_read, size);
                                        total_bytes_read += bytes_from_stream;
                                        size -= bytes_from_stream;
                                    }
                                    while (size > 0);

                                    result = buf;
                                }
                                //There should be a trailing '0'
                                Int32 l_endChar = (Char) stream.ReadByte();
                            }
                            else
                            {
                                //it must have been a '0', thus no results
                            }
                            break;

                        case 'Z':
                            l_endQuery = true;
                            break;

                        default:
                            throw new NpgsqlException(string.Format("postgresql.fp.protocol {0}", c));
                    }
                }

                if (errorMessage != null)
                {
                    throw new NpgsqlException("postgresql.fp.error" + errorMessage);
                }

                return result;
            }
        }

        /*
         * Send a function call to the PostgreSQL backend by name.
         *
         * Note: the mapping for the procedure name to function id needs to exist,
         * usually to an earlier call to addfunction().
         *
         * This is the prefered method to call, as function id's can/may change
         * between versions of the backend.
         *
         * For an example of how this works, refer to org.postgresql.largeobject.LargeObject
         *
         * @param name Function name
         * @param resulttype True if the result is an integer, false for other
         * results
         * @param args FastpathArguments to pass to fastpath
         * @return null if no data, Integer if an integer result, or byte[] otherwise
         * @exception NpgsqlException if name is unknown or if a database-access error
         * occurs.
         * @see org.postgresql.largeobject.LargeObject
         */

        public Object FastpathCall(String name, Boolean resulttype, FastpathArg[] args)
        {
            return FastpathCall(GetID(name), resulttype, args);
        }

        /*
         * This convenience method assumes that the return value is an Integer
         * @param name Function name
         * @param args Function arguments
         * @return integer result
         * @exception NpgsqlException if a database-access error occurs or no result
         */

        public Int32 GetInteger(String name, FastpathArg[] args)
        {
            Int32 i = (Int32) FastpathCall(name, true, args);

            return i;
        }

        /*
         * This convenience method assumes that the return value is an Integer
         * @param name Function name
         * @param args Function arguments
         * @return byte[] array containing result
         * @exception NpgsqlException if a database-access error occurs or no result
         */

        public Byte[] GetData(String name, FastpathArg[] args)
        {
            return (Byte[]) FastpathCall(name, false, args);
        }

        /*
         * This adds a function to our lookup table.
         *
         * <p>User code should use the addFunctions method, which is based upon a
         * query, rather than hard coding the oid. The oid for a function is not
         * guaranteed to remain static, even on different servers of the same
         * version.
         *
         * @param name Function name
         * @param fnid Function id
         */

        public void AddFunction(String name, Int32 fnid)
        {
            func.Add(name, fnid);
        }

        /*
         * This takes a ResultSet containing two columns. Column 1 contains the
         * function name, Column 2 the oid.
         *
         * <p>It reads the entire ResultSet, loading the values into the function
         * table.
         *
         * <p><b>REMEMBER</b> to close() the resultset after calling this!!
         *
         * <p><b><em>Implementation note about function name lookups:</em></b>
         *
         * <p>PostgreSQL stores the function id's and their corresponding names in
         * the pg_proc table. To speed things up locally, instead of querying each
         * function from that table when required, a Dictionary is used. Also, only
         * the function's required are entered into this table, keeping connection
         * times as fast as possible.
         *
         * <p>The org.postgresql.largeobject.LargeObject class performs a query upon it's startup,
         * and passes the returned ResultSet to the addFunctions() method here.
         *
         * <p>Once this has been done, the LargeObject api refers to the functions by
         * name.
         *
         * <p>Dont think that manually converting them to the oid's will work. Ok,
         * they will for now, but they can change during development (there was some
         * discussion about this for V7.0), so this is implemented to prevent any
         * unwarranted headaches in the future.
         *
         * @param rs ResultSet
         * @exception NpgsqlException if a database-access error occurs.
         * @see org.postgresql.largeobject.LargeObjectManager
         */

        public void AddFunctions(IDataReader rs)
        {
            while (rs.Read())
            {
                String key = (String) rs[0];
                if (!func.ContainsKey(key))
                {
                    func.Add(key, Int32.Parse(rs[1].ToString()));
                }
            }
        }

        /*
         * This returns the function id associated by its name
         *
         * <p>If addFunction() or addFunctions() have not been called for this name,
         * then an NpgsqlException is thrown.
         *
         * @param name Function name to lookup
         * @return Function ID for fastpath call
         * @exception NpgsqlException is function is unknown.
         */

        public Int32 GetID(String name)
        {
            return func[name];
        }
    }
}
