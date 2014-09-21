package codecs.memory;

/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

import java.io.IOException;
import java.util.ArrayList;
import java.util.BitSet;
import java.util.Collections;
import java.util.Comparator;
import java.util.Iterator;
import java.util.TreeMap;

import codecs.BlockTermState;
import codecs.CodecUtil;
import codecs.FieldsProducer;
import codecs.PostingsReaderBase;
import index.CorruptIndexException;
import index.DocsAndPositionsEnum;
import index.DocsEnum;
import index.FieldInfo.IndexOptions;
import index.FieldInfo;
import index.FieldInfos;
import index.IndexFileNames;
import index.SegmentInfo;
import index.SegmentReadState;
import index.TermState;
import index.Terms;
import index.TermsEnum;
import store.ByteArrayDataInput;
import store.IndexInput;
import util.ArrayUtil;
import util.Bits;
import util.BytesRef;
import util.IOUtils;
import util.RamUsageEstimator;
import util.automaton.ByteRunAutomaton;
import util.automaton.CompiledAutomaton;
import util.fst.BytesRefFSTEnum.InputOutput;
import util.fst.BytesRefFSTEnum;
import util.fst.FST;
import util.fst.Outputs;
import util.fst.Util;

/**
 * FST-based terms dictionary reader.
 *
 * The FST directly maps each term and its metadata, 
 * it is memory resident.
 *
 * @lucene.experimental
 */

public class FSTTermsReader extends FieldsProducer {
  final TreeMap<String, TermsReader> fields = new TreeMap<>();
  final PostingsReaderBase postingsReader;
  //static bool TEST = false;
  final int version;

  public FSTTermsReader(SegmentReadState state, PostingsReaderBase postingsReader)  {
    final String termsFileName = IndexFileNames.segmentFileName(state.segmentInfo.name, state.segmentSuffix, FSTTermsWriter.TERMS_EXTENSION);

    this.postingsReader = postingsReader;
    final IndexInput in = state.directory.openInput(termsFileName, state.context);

    bool success = false;
    try {
      version = readHeader(in);
      if (version >= FSTTermsWriter.TERMS_VERSION_CHECKSUM) {
        CodecUtil.checksumEntireFile(in);
      }
      this.postingsReader.init(in);
      seekDir(in);

      final FieldInfos fieldInfos = state.fieldInfos;
      final int numFields = in.readVInt();
      for (int i = 0; i < numFields; i++) {
        int fieldNumber = in.readVInt();
        FieldInfo fieldInfo = fieldInfos.fieldInfo(fieldNumber);
        long numTerms = in.readVLong();
        long sumTotalTermFreq = fieldInfo.getIndexOptions() == IndexOptions.DOCS_ONLY ? -1 : in.readVLong();
        long sumDocFreq = in.readVLong();
        int docCount = in.readVInt();
        int longsSize = in.readVInt();
        TermsReader current = new TermsReader(fieldInfo, in, numTerms, sumTotalTermFreq, sumDocFreq, docCount, longsSize);
        TermsReader previous = fields.put(fieldInfo.name, current);
        checkFieldSummary(state.segmentInfo, in, current, previous);
      }
      success = true;
    } finally {
      if (success) {
        IOUtils.close(in);
      } else {
        IOUtils.closeWhileHandlingException(in);
      }
    }
  }

  private int readHeader(IndexInput in)  {
    return CodecUtil.checkHeader(in, FSTTermsWriter.TERMS_CODEC_NAME,
                                     FSTTermsWriter.TERMS_VERSION_START,
                                     FSTTermsWriter.TERMS_VERSION_CURRENT);
  }
  private void seekDir(IndexInput in)  {
    if (version >= FSTTermsWriter.TERMS_VERSION_CHECKSUM) {
      in.seek(in.length() - CodecUtil.footerLength() - 8);
    } else {
      in.seek(in.length() - 8);
    }
    in.seek(in.readLong());
  }
  private void checkFieldSummary(SegmentInfo info, IndexInput in, TermsReader field, TermsReader previous)  {
    // #docs with field must be <= #docs
    if (field.docCount < 0 || field.docCount > info.getDocCount()) {
      throw new CorruptIndexException("invalid docCount: " + field.docCount + " maxDoc: " + info.getDocCount() + " (resource=" + in + ")");
    }
    // #postings must be >= #docs with field
    if (field.sumDocFreq < field.docCount) {
      throw new CorruptIndexException("invalid sumDocFreq: " + field.sumDocFreq + " docCount: " + field.docCount + " (resource=" + in + ")");
    }
    // #positions must be >= #postings
    if (field.sumTotalTermFreq != -1 && field.sumTotalTermFreq < field.sumDocFreq) {
      throw new CorruptIndexException("invalid sumTotalTermFreq: " + field.sumTotalTermFreq + " sumDocFreq: " + field.sumDocFreq + " (resource=" + in + ")");
    }
    if (previous != null) {
      throw new CorruptIndexException("duplicate fields: " + field.fieldInfo.name + " (resource=" + in + ")");
    }
  }

  @Override
  public Iterator<String> iterator() {
    return Collections.unmodifiableSet(fields.keySet()).iterator();
  }

  @Override
  public Terms terms(String field)  {
    Debug.Assert( field != null;
    return fields.get(field);
  }

  @Override
  public int size() {
    return fields.size();
  }

  @Override
  public void close()  {
    try {
      IOUtils.close(postingsReader);
    } finally {
      fields.clear();
    }
  }

  final class TermsReader extends Terms {
    final FieldInfo fieldInfo;
    final long numTerms;
    final long sumTotalTermFreq;
    final long sumDocFreq;
    final int docCount;
    final int longsSize;
    final FST<FSTTermOutputs.TermData> dict;

    TermsReader(FieldInfo fieldInfo, IndexInput in, long numTerms, long sumTotalTermFreq, long sumDocFreq, int docCount, int longsSize)  {
      this.fieldInfo = fieldInfo;
      this.numTerms = numTerms;
      this.sumTotalTermFreq = sumTotalTermFreq;
      this.sumDocFreq = sumDocFreq;
      this.docCount = docCount;
      this.longsSize = longsSize;
      this.dict = new FST<>(in, new FSTTermOutputs(fieldInfo, longsSize));
    }

    @Override
    public Comparator<BytesRef> getComparator() {
      return BytesRef.getUTF8SortedAsUnicodeComparator();
    }

    @Override
    public bool hasFreqs() {
      return fieldInfo.getIndexOptions().compareTo(IndexOptions.DOCS_AND_FREQS) >= 0;
    }

    @Override
    public bool hasOffsets() {
      return fieldInfo.getIndexOptions().compareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS) >= 0;
    }

    @Override
    public bool hasPositions() {
      return fieldInfo.getIndexOptions().compareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS) >= 0;
    }

    @Override
    public bool hasPayloads() {
      return fieldInfo.hasPayloads();
    }

    @Override
    public long size() {
      return numTerms;
    }

    @Override
    public long getSumTotalTermFreq() {
      return sumTotalTermFreq;
    }

    @Override
    public long getSumDocFreq()  {
      return sumDocFreq;
    }

    @Override
    public int getDocCount()  {
      return docCount;
    }

    @Override
    public TermsEnum iterator(TermsEnum reuse)  {
      return new SegmentTermsEnum();
    }

    @Override
    public TermsEnum intersect(CompiledAutomaton compiled, BytesRef startTerm)  {
      return new IntersectTermsEnum(compiled, startTerm);
    }

    // Only wraps common operations for PBF interact
    abstract class BaseTermsEnum extends TermsEnum {
      /* Current term, null when enum ends or unpositioned */
      BytesRef term;

      /* Current term stats + decoded metadata (customized by PBF) */
      final BlockTermState state;

      /* Current term stats + undecoded metadata (long[] & byte[]) */
      FSTTermOutputs.TermData meta;
      ByteArrayDataInput bytesReader;

      /** Decodes metadata into customized term state */
      abstract void decodeMetaData() ;

      BaseTermsEnum()  {
        this.state = postingsReader.newTermState();
        this.bytesReader = new ByteArrayDataInput();
        this.term = null;
        // NOTE: metadata will only be initialized in child class
      }

      @Override
      public TermState termState()  {
        decodeMetaData();
        return state.clone();
      }

      @Override
      public BytesRef term() {
        return term;
      }

      @Override
      public int docFreq()  {
        return state.docFreq;
      }

      @Override
      public long totalTermFreq()  {
        return state.totalTermFreq;
      }

      @Override
      public DocsEnum docs(Bits liveDocs, DocsEnum reuse, int flags)  {
        decodeMetaData();
        return postingsReader.docs(fieldInfo, state, liveDocs, reuse, flags);
      }

      @Override
      public DocsAndPositionsEnum docsAndPositions(Bits liveDocs, DocsAndPositionsEnum reuse, int flags)  {
        if (!hasPositions()) {
          return null;
        }
        decodeMetaData();
        return postingsReader.docsAndPositions(fieldInfo, state, liveDocs, reuse, flags);
      }

      @Override
      public void seekExact(long ord)  {
        throw new UnsupportedOperationException();
      }

      @Override
      public long ord() {
        throw new UnsupportedOperationException();
      }
    }


    // Iterates through all terms in this field
    private final class SegmentTermsEnum extends BaseTermsEnum {
      final BytesRefFSTEnum<FSTTermOutputs.TermData> fstEnum;

      /* True when current term's metadata is decoded */
      bool decoded;

      /* True when current enum is 'positioned' by seekExact(TermState) */
      bool seekPending;

      SegmentTermsEnum()  {
        super();
        this.fstEnum = new BytesRefFSTEnum<>(dict);
        this.decoded = false;
        this.seekPending = false;
        this.meta = null;
      }

      @Override
      public Comparator<BytesRef> getComparator() {
        return BytesRef.getUTF8SortedAsUnicodeComparator();
      }

      // Let PBF decode metadata from long[] and byte[]
      @Override
      void decodeMetaData()  {
        if (!decoded && !seekPending) {
          if (meta.bytes != null) {
            bytesReader.reset(meta.bytes, 0, meta.bytes.length);
          }
          postingsReader.decodeTerm(meta.longs, bytesReader, fieldInfo, state, true);
          decoded = true;
        }
      }

      // Update current enum according to FSTEnum
      void updateEnum(final InputOutput<FSTTermOutputs.TermData> pair) {
        if (pair == null) {
          term = null;
        } else {
          term = pair.input;
          meta = pair.output;
          state.docFreq = meta.docFreq;
          state.totalTermFreq = meta.totalTermFreq;
        }
        decoded = false;
        seekPending = false;
      }

      @Override
      public BytesRef next()  {
        if (seekPending) {  // previously positioned, but termOutputs not fetched
          seekPending = false;
          SeekStatus status = seekCeil(term);
          Debug.Assert( status == SeekStatus.FOUND;  // must positioned on valid term
        }
        updateEnum(fstEnum.next());
        return term;
      }

      @Override
      public bool seekExact(BytesRef target)  {
        updateEnum(fstEnum.seekExact(target));
        return term != null;
      }

      @Override
      public SeekStatus seekCeil(BytesRef target)  {
        updateEnum(fstEnum.seekCeil(target));
        if (term == null) {
          return SeekStatus.END;
        } else {
          return term.equals(target) ? SeekStatus.FOUND : SeekStatus.NOT_FOUND;
        }
      }

      @Override
      public void seekExact(BytesRef target, TermState otherState) {
        if (!target.equals(term)) {
          state.copyFrom(otherState);
          term = BytesRef.deepCopyOf(target);
          seekPending = true;
        }
      }
    }

    // Iterates intersect result with automaton (cannot seek!)
    private final class IntersectTermsEnum extends BaseTermsEnum {
      /* True when current term's metadata is decoded */
      bool decoded;

      /* True when there is pending term when calling next() */
      bool pending;

      /* stack to record how current term is constructed, 
       * used to accumulate metadata or rewind term:
       *   level == term.length + 1,
       *         == 0 when term is null */
      Frame[] stack;
      int level;

      /* to which level the metadata is accumulated 
       * so that we can accumulate metadata lazily */
      int metaUpto;

      /* term dict fst */
      final FST<FSTTermOutputs.TermData> fst;
      final FST.BytesReader fstReader;
      final Outputs<FSTTermOutputs.TermData> fstOutputs;

      /* query automaton to intersect with */
      final ByteRunAutomaton fsa;

      private final class Frame {
        /* fst stats */
        FST.Arc<FSTTermOutputs.TermData> fstArc;

        /* automaton stats */
        int fsaState;

        Frame() {
          this.fstArc = new FST.Arc<>();
          this.fsaState = -1;
        }

        public String toString() {
          return "arc=" + fstArc + " state=" + fsaState;
        }
      }

      IntersectTermsEnum(CompiledAutomaton compiled, BytesRef startTerm)  {
        super();
        //if (TEST) System.out.println("Enum init, startTerm=" + startTerm);
        this.fst = dict;
        this.fstReader = fst.getBytesReader();
        this.fstOutputs = dict.outputs;
        this.fsa = compiled.runAutomaton;
        this.level = -1;
        this.stack = new Frame[16];
        for (int i = 0 ; i < stack.length; i++) {
          this.stack[i] = new Frame();
        }

        Frame frame;
        frame = loadVirtualFrame(newFrame());
        this.level++;
        frame = loadFirstFrame(newFrame());
        pushFrame(frame);

        this.meta = null;
        this.metaUpto = 1;
        this.decoded = false;
        this.pending = false;

        if (startTerm == null) {
          pending = isAccept(topFrame());
        } else {
          doSeekCeil(startTerm);
          pending = !startTerm.equals(term) && isValid(topFrame()) && isAccept(topFrame());
        }
      }

      @Override
      public Comparator<BytesRef> getComparator() {
        return BytesRef.getUTF8SortedAsUnicodeComparator();
      }

      @Override
      void decodeMetaData()  {
        Debug.Assert( term != null;
        if (!decoded) {
          if (meta.bytes != null) {
            bytesReader.reset(meta.bytes, 0, meta.bytes.length);
          }
          postingsReader.decodeTerm(meta.longs, bytesReader, fieldInfo, state, true);
          decoded = true;
        }
      }

      /** Lazily accumulate meta data, when we got a accepted term */
      void loadMetaData()  {
        FST.Arc<FSTTermOutputs.TermData> last, next;
        last = stack[metaUpto].fstArc;
        while (metaUpto != level) {
          metaUpto++;
          next = stack[metaUpto].fstArc;
          next.output = fstOutputs.add(next.output, last.output);
          last = next;
        }
        if (last.isFinal()) {
          meta = fstOutputs.add(last.output, last.nextFinalOutput);
        } else {
          meta = last.output;
        }
        state.docFreq = meta.docFreq;
        state.totalTermFreq = meta.totalTermFreq;
      }

      @Override
      public SeekStatus seekCeil(BytesRef target)  {
        decoded = false;
        term = doSeekCeil(target);
        loadMetaData();
        if (term == null) {
          return SeekStatus.END;
        } else {
          return term.equals(target) ? SeekStatus.FOUND : SeekStatus.NOT_FOUND;
        }
      }

      @Override
      public BytesRef next()  {
        //if (TEST) System.out.println("Enum next()");
        if (pending) {
          pending = false;
          loadMetaData();
          return term;
        }
        decoded = false;
      DFS:
        while (level > 0) {
          Frame frame = newFrame();
          if (loadExpandFrame(topFrame(), frame) != null) {  // has valid target
            pushFrame(frame);
            if (isAccept(frame)) {  // gotcha
              break;
            }
            continue;  // check next target
          } 
          frame = popFrame();
          while(level > 0) {
            if (loadNextFrame(topFrame(), frame) != null) {  // has valid sibling 
              pushFrame(frame);
              if (isAccept(frame)) {  // gotcha
                break DFS;
              }
              continue DFS;   // check next target 
            }
            frame = popFrame();
          }
          return null;
        }
        loadMetaData();
        return term;
      }

      private BytesRef doSeekCeil(BytesRef target)  {
        //if (TEST) System.out.println("Enum doSeekCeil()");
        Frame frame= null;
        int label, upto = 0, limit = target.length;
        while (upto < limit) {  // to target prefix, or ceil label (rewind prefix)
          frame = newFrame();
          label = target.bytes[upto] & 0xff;
          frame = loadCeilFrame(label, topFrame(), frame);
          if (frame == null || frame.fstArc.label != label) {
            break;
          }
          Debug.Assert( isValid(frame);  // target must be fetched from automaton
          pushFrame(frame);
          upto++;
        }
        if (upto == limit) {  // got target
          return term;
        }
        if (frame != null) {  // got larger term('s prefix)
          pushFrame(frame);
          return isAccept(frame) ? term : next();
        }
        while (level > 0) {  // got target's prefix, advance to larger term
          frame = popFrame();
          while (level > 0 && !canRewind(frame)) {
            frame = popFrame();
          }
          if (loadNextFrame(topFrame(), frame) != null) {
            pushFrame(frame);
            return isAccept(frame) ? term : next();
          }
        }
        return null;
      }

      /** Virtual frame, never pop */
      Frame loadVirtualFrame(Frame frame)  {
        frame.fstArc.output = fstOutputs.getNoOutput();
        frame.fstArc.nextFinalOutput = fstOutputs.getNoOutput();
        frame.fsaState = -1;
        return frame;
      }

      /** Load frame for start arc(node) on fst */
      Frame loadFirstFrame(Frame frame)  {
        frame.fstArc = fst.getFirstArc(frame.fstArc);
        frame.fsaState = fsa.getInitialState();
        return frame;
      }

      /** Load frame for target arc(node) on fst */
      Frame loadExpandFrame(Frame top, Frame frame)  {
        if (!canGrow(top)) {
          return null;
        }
        frame.fstArc = fst.readFirstRealTargetArc(top.fstArc.target, frame.fstArc, fstReader);
        frame.fsaState = fsa.step(top.fsaState, frame.fstArc.label);
        //if (TEST) System.out.println(" loadExpand frame="+frame);
        if (frame.fsaState == -1) {
          return loadNextFrame(top, frame);
        }
        return frame;
      }

      /** Load frame for sibling arc(node) on fst */
      Frame loadNextFrame(Frame top, Frame frame)  {
        if (!canRewind(frame)) {
          return null;
        }
        while (!frame.fstArc.isLast()) {
          frame.fstArc = fst.readNextRealArc(frame.fstArc, fstReader);
          frame.fsaState = fsa.step(top.fsaState, frame.fstArc.label);
          if (frame.fsaState != -1) {
            break;
          }
        }
        //if (TEST) System.out.println(" loadNext frame="+frame);
        if (frame.fsaState == -1) {
          return null;
        }
        return frame;
      }

      /** Load frame for target arc(node) on fst, so that 
       *  arc.label >= label and !fsa.reject(arc.label) */
      Frame loadCeilFrame(int label, Frame top, Frame frame)  {
        FST.Arc<FSTTermOutputs.TermData> arc = frame.fstArc;
        arc = Util.readCeilArc(label, fst, top.fstArc, arc, fstReader);
        if (arc == null) {
          return null;
        }
        frame.fsaState = fsa.step(top.fsaState, arc.label);
        //if (TEST) System.out.println(" loadCeil frame="+frame);
        if (frame.fsaState == -1) {
          return loadNextFrame(top, frame);
        }
        return frame;
      }

      bool isAccept(Frame frame) {  // reach a term both fst&fsa accepts
        return fsa.isAccept(frame.fsaState) && frame.fstArc.isFinal();
      }
      bool isValid(Frame frame) {   // reach a prefix both fst&fsa won't reject
        return /*frame != null &&*/ frame.fsaState != -1;
      }
      bool canGrow(Frame frame) {   // can walk forward on both fst&fsa
        return frame.fsaState != -1 && FST.targetHasArcs(frame.fstArc);
      }
      bool canRewind(Frame frame) { // can jump to sibling
        return !frame.fstArc.isLast();
      }

      void pushFrame(Frame frame) {
        term = grow(frame.fstArc.label);
        level++;
        //if (TEST) System.out.println("  term=" + term + " level=" + level);
      }

      Frame popFrame() {
        term = shrink();
        level--;
        metaUpto = metaUpto > level ? level : metaUpto;
        //if (TEST) System.out.println("  term=" + term + " level=" + level);
        return stack[level+1];
      }

      Frame newFrame() {
        if (level+1 == stack.length) {
          final Frame[] temp = new Frame[ArrayUtil.oversize(level+2, RamUsageEstimator.NUM_BYTES_OBJECT_REF)];
          System.arraycopy(stack, 0, temp, 0, stack.length);
          for (int i = stack.length; i < temp.length; i++) {
            temp[i] = new Frame();
          }
          stack = temp;
        }
        return stack[level+1];
      }

      Frame topFrame() {
        return stack[level];
      }

      BytesRef grow(int label) {
        if (term == null) {
          term = new BytesRef(new byte[16], 0, 0);
        } else {
          if (term.length == term.bytes.length) {
            term.grow(term.length+1);
          }
          term.bytes[term.length++] = (byte)label;
        }
        return term;
      }

      BytesRef shrink() {
        if (term.length == 0) {
          term = null;
        } else {
          term.length--;
        }
        return term;
      }
    }
  }

  static<T> void walk(FST<T> fst)  {
    final ArrayList<FST.Arc<T>> queue = new ArrayList<>();
    final BitSet seen = new BitSet();
    final FST.BytesReader reader = fst.getBytesReader();
    final FST.Arc<T> startArc = fst.getFirstArc(new FST.Arc<T>());
    queue.add(startArc);
    while (!queue.isEmpty()) {
      final FST.Arc<T> arc = queue.remove(0);
      final long node = arc.target;
      //System.out.println(arc);
      if (FST.targetHasArcs(arc) && !seen.get((int) node)) {
        seen.set((int) node);
        fst.readFirstRealTargetArc(node, arc, reader);
        while (true) {
          queue.add(new FST.Arc<T>().copyFrom(arc));
          if (arc.isLast()) {
            break;
          } else {
            fst.readNextRealArc(arc, reader);
          }
        }
      }
    }
  }

  @Override
  public long ramBytesUsed() {
    long ramBytesUsed = 0;
    for (TermsReader r : fields.values()) {
      ramBytesUsed += r.dict == null ? 0 : r.dict.sizeInBytes();
    }
    return ramBytesUsed;
  }
  
  @Override
  public void checkIntegrity()  {
    postingsReader.checkIntegrity();
  }
}
